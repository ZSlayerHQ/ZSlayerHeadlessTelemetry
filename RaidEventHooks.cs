using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using UnityEngine;

namespace ZSlayerHeadlessTelemetry;

public class RaidEventHooks
{
    private readonly TelemetryReporter _reporter;
    private bool _inRaid;
    private string _currentMap = "";
    private Coroutine _periodicCoroutine;
    private Coroutine _positionCoroutine;
    private MonoBehaviour _coroutineHost;
    private float _positionIntervalSec = 1.0f; // default 1s, configurable via server

    // Cached references from events (singletons may not work on headless)
    private CoopGame _cachedGame;
    private CoopHandler _cachedCoopHandler;

    // System info (collected once, sent with every performance report)
    private object _systemInfo;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuCheck = DateTime.UtcNow;

    // FPS tracking
    private float _fpsMin = float.MaxValue;
    private float _fpsMax;
    private float _fpsSum;
    private int _fpsCount;

    // Bot tracking tick counter (report bots every other tick = ~10s)
    private int _tickCount;

    // Kill counter for raid summary
    private int _raidKillCount;

    // Per-player kill tracking (keyed by profileId)
    private readonly Dictionary<string, int> _playerKillCounts = new();
    private readonly Dictionary<string, (int pmc, int scav, int boss)> _playerKillTypes = new();

    // XP tracking (snapshot at raid start, diff at end)
    private readonly Dictionary<string, long> _xpSnapshots = new();

    // Track registered death handlers to avoid duplicates
    private readonly HashSet<int> _registeredDeathHandlers = new();

    // Wall clock fallback for raid timer
    private DateTime _raidStartWallClock = DateTime.MinValue;

    // Reflection cache for accessing protected Player fields
    private static readonly FieldInfo LastAggressorField =
        typeof(Player).GetField("LastAggressor", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo LastDamageInfoField =
        typeof(Player).GetField("LastDamageInfo", BindingFlags.NonPublic | BindingFlags.Instance);

    public RaidEventHooks(TelemetryReporter reporter)
    {
        _reporter = reporter;
    }

    public void Subscribe()
    {
        FikaEventDispatcher.SubscribeEvent<FikaGameCreatedEvent>(OnGameCreated);
        FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
        FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
    }

    public void Unsubscribe()
    {
        StopPeriodicReporting();
    }

    private void OnGameCreated(FikaGameCreatedEvent e)
    {
        try
        {
            var game = e.Game as CoopGame;
            if (game == null) return;

            _cachedGame = game;
            // Try multiple location sources — Location_0.Id may be null on headless
            _currentMap = "";
            try { _currentMap = Singleton<GameWorld>.Instance?.LocationId ?? ""; } catch { }
            if (string.IsNullOrEmpty(_currentMap))
                try { _currentMap = game.Location_0?.Id ?? ""; } catch { }
            _inRaid = false;
            _raidKillCount = 0;
            _playerKillCounts.Clear();
            _playerKillTypes.Clear();
            _xpSnapshots.Clear();
            DamageTracker.Reset();
            ResetFpsTracking();
            _tickCount = 0;
            _registeredDeathHandlers.Clear();

            _reporter.Post("raid-state", new
            {
                status = "loading",
                map = _currentMap,
                raidTimer = 0,
                raidTimeLeft = 0,
                timeOfDay = "",
                weather = "",
                players = new { pmcAlive = 0, pmcDead = 0, scavAlive = 0, total = 0 }
            });

            Plugin.Log.LogInfo($"[ZSlayerHQ] Game created — map: {_currentMap}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] OnGameCreated error: {ex.Message}");
        }
    }

    private void OnRaidStarted(FikaRaidStartedEvent e)
    {
        try
        {
            _inRaid = true;
            _raidStartWallClock = DateTime.UtcNow;

            // Cache CoopHandler reference for periodic reports
            CoopHandler.TryGetCoopHandler(out _cachedCoopHandler);

            // Retry map detection if it was empty at game creation
            if (string.IsNullOrEmpty(_currentMap))
            {
                try { _currentMap = Singleton<GameWorld>.Instance?.LocationId ?? ""; } catch { }
            }

            // Snapshot XP for all human players at raid start
            try
            {
                if (_cachedCoopHandler != null)
                {
                    foreach (var player in _cachedCoopHandler.HumanPlayers ?? new List<FikaPlayer>())
                    {
                        var profileId = player.ProfileId ?? "";
                        var xp = player.Profile?.Info?.Experience ?? 0;
                        if (!string.IsNullOrEmpty(profileId))
                            _xpSnapshots[profileId] = xp;
                    }
                    Plugin.Log.LogInfo($"[ZSlayerHQ] XP snapshots taken for {_xpSnapshots.Count} players, map: {_currentMap}");
                }
            }
            catch (Exception xpEx)
            {
                Plugin.Log.LogWarning($"[ZSlayerHQ] XP snapshot error: {xpEx.Message}");
            }

            _reporter.Post("raid-state", new
            {
                status = "in-raid",
                map = _currentMap,
                raidTimer = 0,
                raidTimeLeft = 0,
                timeOfDay = "",
                weather = "",
                players = new { pmcAlive = 0, pmcDead = 0, scavAlive = 0, total = 0 }
            });

            StartPeriodicReporting();

            Plugin.Log.LogInfo("[ZSlayerHQ] Raid started — periodic telemetry active");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] OnRaidStarted error: {ex.Message}");
        }
    }

    private void OnGameEnded(FikaGameEndedEvent e)
    {
        try
        {
            _inRaid = false;
            StopPeriodicReporting();

            Plugin.Log.LogInfo($"[ZSlayerHQ] OnGameEnded fired — exitStatus: {e.ExitStatus}, exitName: {e.ExitName}, map: {_currentMap}, kills: {_raidKillCount}");

            SendRaidSummary(e.ExitStatus, e.ExitName);

            _reporter.Post("raid-state", new
            {
                status = "idle",
                map = _currentMap,
                raidTimer = 0,
                raidTimeLeft = 0,
                timeOfDay = "",
                weather = "",
                players = new { pmcAlive = 0, pmcDead = 0, scavAlive = 0, total = 0 }
            });

            _registeredDeathHandlers.Clear();

            Plugin.Log.LogInfo("[ZSlayerHQ] Raid ended — telemetry stopped, summary sent");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] OnGameEnded error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Periodic Reporting (coroutine every 5 seconds)
    // ══════════════════════════════════════════════════════════

    private void StartPeriodicReporting()
    {
        _coroutineHost = UnityEngine.Object.FindObjectOfType<Plugin>();
        if (_coroutineHost == null)
        {
            var go = new GameObject("ZSlayerTelemetry");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _coroutineHost = go.AddComponent<CoroutineHelper>();
        }

        // Fetch configured map refresh rate from server
        FetchMapRefreshRate();

        _periodicCoroutine = _coroutineHost.StartCoroutine(PeriodicReportLoop());
        _positionCoroutine = _coroutineHost.StartCoroutine(PositionReportLoop());
    }

    private void StopPeriodicReporting()
    {
        if (_periodicCoroutine != null && _coroutineHost != null)
        {
            _coroutineHost.StopCoroutine(_periodicCoroutine);
            _periodicCoroutine = null;
        }
        if (_positionCoroutine != null && _coroutineHost != null)
        {
            _coroutineHost.StopCoroutine(_positionCoroutine);
            _positionCoroutine = null;
        }
    }

    private void FetchMapRefreshRate()
    {
        try
        {
            // Ask server for the configured position update interval
            var url = _reporter.BaseUrl + "/map-refresh-rate";
            using var handler = new System.Net.Http.HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
            var task = http.GetStringAsync(url);
            task.Wait(2000);
            if (task.IsCompleted)
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(task.Result);
                var rate = (float?)json["intervalSec"] ?? 1.0f;
                _positionIntervalSec = Mathf.Clamp(rate, 0.05f, 10f);
                Plugin.Log.LogInfo($"[ZSlayerHQ] Map refresh rate: {_positionIntervalSec}s");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] Failed to fetch map refresh rate, using default {_positionIntervalSec}s: {ex.Message}");
        }
    }

    private IEnumerator PeriodicReportLoop()
    {
        while (_inRaid)
        {
            yield return new WaitForSeconds(5f);

            if (!_inRaid) yield break;

            // Each report is isolated so one failure doesn't cascade to the rest
            _tickCount++;
            try { ReportRaidState(); } catch (Exception ex) { Plugin.Log.LogWarning($"[ZSlayerHQ] RaidState error: {ex.Message}"); }
            try { ReportPlayers(); } catch (Exception ex) { Plugin.Log.LogWarning($"[ZSlayerHQ] Players error: {ex.Message}"); }
            try { ReportPerformance(); } catch (Exception ex) { Plugin.Log.LogWarning($"[ZSlayerHQ] Performance error: {ex.Message}"); }
            if (_tickCount == 1 || _tickCount % 2 == 0)
            {
                try { ReportBots(); } catch (Exception ex) { Plugin.Log.LogWarning($"[ZSlayerHQ] Bots error: {ex.Message}"); }
                try { ReportDamageStats(); } catch (Exception ex) { Plugin.Log.LogWarning($"[ZSlayerHQ] DamageStats error: {ex.Message}"); }
            }
            try { RegisterDeathHandlers(); } catch (Exception ex) { Plugin.Log.LogWarning($"[ZSlayerHQ] DeathHandlers error: {ex.Message}"); }
        }
    }

    private IEnumerator PositionReportLoop()
    {
        while (_inRaid)
        {
            yield return new WaitForSeconds(_positionIntervalSec);
            if (!_inRaid) yield break;

            try
            {
                ReportPositions();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ZSlayerHQ] Position report error: {ex.Message}");
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Raid State
    // ══════════════════════════════════════════════════════════

    private void ReportRaidState()
    {
        var game = _cachedGame;
        if (game == null)
        {
            // Fallback: try singleton
            try { game = Singleton<IFikaGame>.Instance as CoopGame; } catch { }
            if (game == null)
            {
                try { game = Singleton<AbstractGame>.Instance as CoopGame; } catch { }
            }
            if (game != null) _cachedGame = game;
        }

        // Player counts
        int pmcAlive = 0, pmcDead = 0, totalHumans = 0;
        try
        {
            var coopHandler = _cachedCoopHandler;
            if (coopHandler == null) CoopHandler.TryGetCoopHandler(out coopHandler);
            if (coopHandler != null)
            {
                var humans = coopHandler.HumanPlayers ?? new List<FikaPlayer>();
                pmcAlive = humans.Count(p => !p.IsAI && p.HealthController?.IsAlive == true);
                pmcDead = humans.Count(p => !p.IsAI && p.HealthController?.IsAlive == false);
                totalHumans = humans.Count;
            }
        }
        catch { /* disposed player — use 0 */ }

        // Raid timer
        int raidTimer = 0;
        int raidTimeLeft = 0;
        try
        {
            if (game?.GameTimer != null)
            {
                raidTimer = (int)game.GameTimer.PastTime.TotalSeconds;
                if (game.GameTimer.SessionTime.HasValue)
                    raidTimeLeft = Math.Max(0, (int)(game.GameTimer.SessionTime.Value.TotalSeconds - raidTimer));
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] GameTimer error: {ex.Message}");
        }

        // Wall-clock fallback — always works even if GameTimer is broken
        if (raidTimer == 0 && _raidStartWallClock != DateTime.MinValue)
        {
            raidTimer = (int)(DateTime.UtcNow - _raidStartWallClock).TotalSeconds;
        }

        // Time of day
        var timeOfDay = "";
        try
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld?.GameDateTime != null)
            {
                var dt = gameWorld.GameDateTime.Calculate();
                timeOfDay = dt.ToString("HH:mm");
            }
        }
        catch { /* ignore */ }

        if (string.IsNullOrEmpty(timeOfDay))
        {
            try
            {
                var gameWorld = Singleton<GameWorld>.Instance;
                if (gameWorld != null)
                {
                    var gdtProp = gameWorld.GetType().GetProperty("GameDateTime");
                    var gdt = gdtProp?.GetValue(gameWorld);
                    if (gdt != null)
                    {
                        var calcMethod = gdt.GetType().GetMethod("Calculate");
                        if (calcMethod != null)
                        {
                            var result = calcMethod.Invoke(gdt, null);
                            if (result is DateTime dtResult)
                                timeOfDay = dtResult.ToString("HH:mm");
                        }
                    }
                }
            }
            catch { /* ignore */ }
        }

        // Always post — never return early without posting
        _reporter.Post("raid-state", new
        {
            status = "in-raid",
            map = _currentMap,
            raidTimer,
            raidTimeLeft,
            timeOfDay,
            weather = "",
            players = new
            {
                pmcAlive,
                pmcDead,
                scavAlive = 0,
                total = totalHumans
            }
        });
    }

    // ══════════════════════════════════════════════════════════
    //  Player Status
    // ══════════════════════════════════════════════════════════

    private void ReportPlayers()
    {
        var coopHandler = _cachedCoopHandler;
        if (coopHandler == null && !CoopHandler.TryGetCoopHandler(out coopHandler)) return;

        var humans = coopHandler.HumanPlayers;
        if (humans == null) return;

        // Try to get ping data from FikaServer
        Dictionary<int, int> peerPings = null;
        try
        {
            var server = Singleton<FikaServer>.Instance;
            if (server?.NetServer != null)
            {
                peerPings = new Dictionary<int, int>();
                foreach (var peer in server.NetServer.ConnectedPeerList)
                {
                    peerPings[peer.Id] = peer.Ping;
                }
            }
        }
        catch { /* ignore ping lookup failures */ }

        var playerList = new List<object>();
        foreach (var player in humans)
        {
            try
            {
                var health = GetPlayerHealthPercent(player);
                int? pingMs = null;

                // Host/headless player is local — show 0ms ping
                if (player.IsYourPlayer)
                {
                    pingMs = 0;
                }
                else if (peerPings != null && peerPings.Count > 0)
                {
                    // Try matching by NetId first
                    if (peerPings.TryGetValue(player.NetId, out var ping))
                        pingMs = ping;
                    // Fallback: if only one unmatched peer, assign it
                    else if (peerPings.Count == 1)
                    {
                        foreach (var p in peerPings.Values) { pingMs = p; break; }
                    }
                }

                playerList.Add(new
                {
                    name = player.Profile?.Nickname ?? "",
                    profileId = player.ProfileId ?? "",
                    type = GetPlayerType(player),
                    side = player.Side.ToString().ToLowerInvariant(),
                    level = player.Profile?.Info?.Level ?? 0,
                    alive = player.HealthController?.IsAlive ?? false,
                    health,
                    pingMs
                });
            }
            catch { /* skip player on error */ }
        }

        _reporter.Post("players", new
        {
            map = _currentMap,
            players = playerList
        });
    }

    // ══════════════════════════════════════════════════════════
    //  Performance (FPS)
    // ══════════════════════════════════════════════════════════

    private void ReportPerformance()
    {
        var fps = Mathf.RoundToInt(1f / Time.deltaTime);
        UpdateFpsTracking(fps);

        // Calculate CPU usage from process time delta
        double cpuUsage = 0;
        try
        {
            var proc = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = proc.TotalProcessorTime;
            var elapsed = (now - _lastCpuCheck).TotalMilliseconds;
            if (elapsed > 0 && _lastCpuTime.TotalMilliseconds > 0)
            {
                var cpuDelta = (cpuTime - _lastCpuTime).TotalMilliseconds;
                cpuUsage = Math.Round(cpuDelta / elapsed / Environment.ProcessorCount * 100, 1);
                cpuUsage = Math.Min(cpuUsage, 100);
            }
            _lastCpuTime = cpuTime;
            _lastCpuCheck = now;
        }
        catch { /* ignore */ }

        // Get memory — try process working set first, fall back to GC
        long memoryMb = 0;
        try
        {
            var ws = Process.GetCurrentProcess().WorkingSet64;
            memoryMb = ws > 0 ? ws / (1024 * 1024) : GC.GetTotalMemory(false) / (1024 * 1024);
        }
        catch
        {
            memoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
        }

        // Collect system info once
        if (_systemInfo == null)
        {
            _systemInfo = new
            {
                cpuModel = SystemInfo.processorType ?? "",
                cpuCores = SystemInfo.processorCount,
                cpuFrequencyMhz = SystemInfo.processorFrequency,
                gpuModel = SystemInfo.graphicsDeviceName ?? "",
                gpuVramMb = SystemInfo.graphicsMemorySize,
                totalRamMb = SystemInfo.systemMemorySize,
                os = SystemInfo.operatingSystem ?? ""
            };
        }

        _reporter.Post("performance", new
        {
            fps,
            fpsAvg = _fpsCount > 0 ? Mathf.RoundToInt(_fpsSum / _fpsCount) : fps,
            fpsMin = _fpsMin < float.MaxValue ? Mathf.RoundToInt(_fpsMin) : fps,
            fpsMax = Mathf.RoundToInt(_fpsMax),
            frameTimeMs = Math.Round(Time.deltaTime * 1000f, 1),
            memoryMb,
            cpuUsage,
            systemInfo = _systemInfo
        });
    }

    private void UpdateFpsTracking(float fps)
    {
        if (fps < _fpsMin) _fpsMin = fps;
        if (fps > _fpsMax) _fpsMax = fps;
        _fpsSum += fps;
        _fpsCount++;
    }

    private void ResetFpsTracking()
    {
        _fpsMin = float.MaxValue;
        _fpsMax = 0;
        _fpsSum = 0;
        _fpsCount = 0;
    }

    // ══════════════════════════════════════════════════════════
    //  Bot Counts
    // ══════════════════════════════════════════════════════════

    private void ReportBots()
    {
        var coopHandler = _cachedCoopHandler;
        if (coopHandler == null && !CoopHandler.TryGetCoopHandler(out coopHandler)) return;

        var allPlayers = coopHandler.Players;
        if (allPlayers == null) return;

        int scavsAlive = 0, scavsDead = 0;
        int raidersAlive = 0, raidersDead = 0;
        int roguesAlive = 0, roguesDead = 0;
        var bosses = new List<object>();

        foreach (var kvp in allPlayers)
        {
            var player = kvp.Value;
            if (player == null || !player.IsAI) continue;

            try
            {
                var role = player.Profile?.Info?.Settings?.Role ?? WildSpawnType.marksman;
                var alive = player.HealthController?.IsAlive ?? false;

                // Check if boss
                if (LocaleUtils.IsBoss(role, out var bossName))
                {
                    string killedBy = null;
                    if (!alive)
                    {
                        try
                        {
                            var aggressor = LastAggressorField?.GetValue(player) as IPlayer;
                            killedBy = aggressor?.Profile?.Nickname;
                        }
                        catch { /* ignore */ }
                    }

                    bosses.Add(new { name = bossName, alive, killedBy });
                    continue;
                }

                // Categorize by role
                switch (role)
                {
                    case WildSpawnType.pmcBot:
                    case WildSpawnType.exUsec:
                        if (alive) roguesAlive++; else roguesDead++;
                        break;
                    case WildSpawnType.arenaFighter:
                    case WildSpawnType.crazyAssaultEvent:
                        if (alive) raidersAlive++; else raidersDead++;
                        break;
                    case WildSpawnType.assault:
                    case WildSpawnType.marksman:
                    case WildSpawnType.cursedAssault:
                    case WildSpawnType.assaultGroup:
                    default:
                        if (alive) scavsAlive++; else scavsDead++;
                        break;
                }
            }
            catch { /* skip disposed/invalid bot */ }
        }

        var totalAlive = scavsAlive + raidersAlive + roguesAlive;
        var totalDead = scavsDead + raidersDead + roguesDead;

        // Log bot counts for diagnostics (only every 6th tick = ~30s)
        if (_tickCount % 6 == 0)
        {
            var playerCount = allPlayers?.Count ?? 0;
            var aiCount = allPlayers?.Values.Count(p => p != null && p.IsAI) ?? 0;
            Plugin.Log.LogInfo($"[ZSlayerHQ] Bots: {playerCount} total players, {aiCount} AI, scav={scavsAlive}/{scavsDead} raider={raidersAlive}/{raidersDead} rogue={roguesAlive}/{roguesDead} boss={bosses.Count} totalAI={totalAlive}/{totalDead}");
        }

        _reporter.Post("bots", new
        {
            map = _currentMap,
            scavs = new { alive = scavsAlive, dead = scavsDead },
            raiders = new { alive = raidersAlive, dead = raidersDead },
            rogues = new { alive = roguesAlive, dead = roguesDead },
            bosses,
            totalAI = new
            {
                alive = totalAlive,
                dead = totalDead
            }
        });
    }

    // ══════════════════════════════════════════════════════════
    //  Positions (for live minimap)
    // ══════════════════════════════════════════════════════════

    private void ReportPositions()
    {
        var coopHandler = _cachedCoopHandler;
        if (coopHandler == null && !CoopHandler.TryGetCoopHandler(out coopHandler)) return;

        var allPlayers = coopHandler.Players;
        if (allPlayers == null) return;

        var positions = new List<object>();

        foreach (var kvp in allPlayers)
        {
            var player = kvp.Value;
            if (player == null) continue;

            try
            {
                var pos = player.Position;
                var rot = player.Rotation;
                var alive = player.HealthController?.IsAlive ?? false;

                positions.Add(new
                {
                    name = player.Profile?.Nickname ?? "",
                    profileId = player.ProfileId ?? "",
                    type = GetPlayerType(player),
                    x = Math.Round(pos.x, 1),
                    y = Math.Round(pos.y, 1),  // elevation
                    z = Math.Round(pos.z, 1),
                    rotation = Math.Round(rot.x, 1),  // horizontal facing
                    alive,
                    side = player.Side.ToString().ToLowerInvariant()
                });
            }
            catch { /* skip on error */ }
        }

        _reporter.Post("positions", new
        {
            map = _currentMap,
            positions
        });
    }

    // ══════════════════════════════════════════════════════════
    //  Damage Stats
    // ══════════════════════════════════════════════════════════

    private void ReportDamageStats()
    {
        if (DamageTracker.TotalHits == 0) return;
        _reporter.Post("damage-stats", DamageTracker.ToPayload());
    }

    // ══════════════════════════════════════════════════════════
    //  Kill Events (registered on players)
    // ══════════════════════════════════════════════════════════

    private void RegisterDeathHandlers()
    {
        var coopHandler = _cachedCoopHandler;
        if (coopHandler == null && !CoopHandler.TryGetCoopHandler(out coopHandler)) return;

        var allPlayers = coopHandler.Players;
        if (allPlayers == null) return;

        foreach (var kvp in allPlayers)
        {
            var player = kvp.Value;
            if (player == null) continue;
            if (_registeredDeathHandlers.Contains(player.NetId)) continue;

            _registeredDeathHandlers.Add(player.NetId);

            player.OnPlayerDeadOrUnspawn += (deadPlayer) =>
            {
                try
                {
                    OnPlayerDied(deadPlayer as FikaPlayer);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[ZSlayerHQ] Death handler error: {ex.Message}");
                }
            };
        }
    }

    private void OnPlayerDied(FikaPlayer victim)
    {
        if (victim == null) return;

        _raidKillCount++;

        var game = Singleton<IFikaGame>.Instance as CoopGame;
        int raidTime = 0;
        if (game?.GameTimer != null)
            raidTime = (int)game.GameTimer.PastTime.TotalSeconds;

        // Get killer info via reflection (protected fields)
        IPlayer killerPlayer = null;
        string weaponTpl = "";
        string ammoTpl = "";

        try
        {
            killerPlayer = LastAggressorField?.GetValue(victim) as IPlayer;
        }
        catch { /* ignore */ }

        try
        {
            var damageInfoObj = LastDamageInfoField?.GetValue(victim);
            if (damageInfoObj is DamageInfoStruct damageInfo)
            {
                if (damageInfo.Weapon != null)
                    weaponTpl = damageInfo.Weapon.TemplateId.ToString();

                // Get ammo template ID from SourceId
                if (!string.IsNullOrEmpty(damageInfo.SourceId))
                    ammoTpl = damageInfo.SourceId;
            }
        }
        catch { /* ignore */ }

        // Use LastDamagedBodyPart (public EBodyPart field) for reliable body part detection
        var bodyPartEnum = victim.LastDamagedBodyPart;
        var bodyPart = GetBodyPartName(bodyPartEnum);
        var isHeadshot = bodyPartEnum == EBodyPart.Head;

        // Calculate distance
        double distance = 0;
        FikaPlayer killerFikaPlayer = killerPlayer as FikaPlayer;
        if (killerFikaPlayer != null)
        {
            try
            {
                distance = Math.Round(Vector3.Distance(killerFikaPlayer.Position, victim.Position), 1);
            }
            catch { /* ignore */ }
        }

        // Fallback: use KillerId (public property) to find killer name
        string killerName = killerPlayer?.Profile?.Nickname ?? "";
        string killerType = killerFikaPlayer != null ? GetPlayerType(killerFikaPlayer) : "unknown";
        int killerLevel = killerPlayer?.Profile?.Info?.Level ?? 0;

        if (killerPlayer == null && !string.IsNullOrEmpty(victim.KillerId))
        {
            // Try to resolve killer from KillerId
            var ch = _cachedCoopHandler;
            if (ch != null || CoopHandler.TryGetCoopHandler(out ch))
            {
                foreach (var kvp in ch.Players)
                {
                    if (kvp.Value?.ProfileId == victim.KillerId)
                    {
                        killerName = kvp.Value.Profile?.Nickname ?? "";
                        killerType = GetPlayerType(kvp.Value);
                        killerLevel = kvp.Value.Profile?.Info?.Level ?? 0;
                        try { distance = Math.Round(Vector3.Distance(kvp.Value.Position, victim.Position), 1); } catch { }
                        break;
                    }
                }
            }
        }

        // Track per-player kill counts and types
        var killerProfileId = killerPlayer?.ProfileId ?? victim.KillerId ?? "";
        if (!string.IsNullOrEmpty(killerProfileId))
        {
            _playerKillCounts.TryGetValue(killerProfileId, out var count);
            _playerKillCounts[killerProfileId] = count + 1;

            var victimType = GetPlayerType(victim);
            _playerKillTypes.TryGetValue(killerProfileId, out var types);
            _playerKillTypes[killerProfileId] = victimType switch
            {
                "pmc" => (types.pmc + 1, types.scav, types.boss),
                "boss" => (types.pmc, types.scav, types.boss + 1),
                _ => (types.pmc, types.scav + 1, types.boss) // scav, raider, rogue, follower
            };
        }

        Plugin.Log.LogInfo($"[ZSlayerHQ] Kill: {killerName}({killerType}) → {victim.Profile?.Nickname}({GetPlayerType(victim)}) [{weaponTpl}] body:{bodyPart} dist:{distance}m hs:{isHeadshot}");

        _reporter.Post("kill", new
        {
            timestamp = DateTime.UtcNow,
            raidTime,
            map = _currentMap,
            killer = new
            {
                name = killerName,
                type = killerType,
                level = killerLevel
            },
            victim = new
            {
                name = victim.Profile?.Nickname ?? "",
                type = GetPlayerType(victim),
                level = victim.Profile?.Info?.Level ?? 0
            },
            weapon = weaponTpl,
            ammo = ammoTpl,
            bodyPart,
            distance,
            isHeadshot
        });

        // Also report as extract event if it's a human player death
        if (!victim.IsObservedAI)
        {
            _reporter.Post("extract", new
            {
                timestamp = DateTime.UtcNow,
                map = _currentMap,
                raidTime,
                player = new
                {
                    name = victim.Profile?.Nickname ?? "",
                    profileId = victim.ProfileId ?? "",
                    type = GetPlayerType(victim),
                    side = victim.Side.ToString().ToLowerInvariant(),
                    level = victim.Profile?.Info?.Level ?? 0
                },
                outcome = "killed",
                extractPoint = (string)null,
                killedBy = killerName,
                raidDuration = raidTime
            });
        }
    }

    // ══════════════════════════════════════════════════════════
    //  Raid Summary
    // ══════════════════════════════════════════════════════════

    private void SendRaidSummary(ExitStatus exitStatus, string exitName)
    {
        Plugin.Log.LogInfo($"[ZSlayerHQ] SendRaidSummary starting — exitStatus: {exitStatus}, exitName: {exitName}");

        var coopHandler = _cachedCoopHandler;
        if (coopHandler == null && !CoopHandler.TryGetCoopHandler(out coopHandler))
        {
            Plugin.Log.LogWarning("[ZSlayerHQ] SendRaidSummary: CoopHandler not available!");
            return;
        }

        var game = Singleton<IFikaGame>.Instance as CoopGame;
        int raidDuration = 0;
        if (game?.GameTimer != null)
            raidDuration = (int)game.GameTimer.PastTime.TotalSeconds;

        var humans = coopHandler.HumanPlayers ?? new List<FikaPlayer>();
        var playerSummaries = new List<object>();

        foreach (var player in humans)
        {
            try
            {
                var alive = player.HealthController?.IsAlive ?? false;
                var extracted = coopHandler.ExtractedPlayers?.Contains(player.NetId) ?? false;
                var outcome = alive && extracted ? "survived" : alive ? "mia" : "killed";

                var profileId = player.ProfileId ?? "";
                _playerKillCounts.TryGetValue(profileId, out var kills);
                _playerKillTypes.TryGetValue(profileId, out var killTypes);

                // Calculate XP earned (diff from snapshot)
                int xpEarned = 0;
                try
                {
                    var currentXp = player.Profile?.Info?.Experience ?? 0;
                    if (_xpSnapshots.TryGetValue(profileId, out var startXp) && currentXp > startXp)
                        xpEarned = (int)(currentXp - startXp);
                }
                catch { /* ignore */ }

                playerSummaries.Add(new
                {
                    name = player.Profile?.Nickname ?? "",
                    outcome,
                    extractPoint = extracted ? exitName : (string)null,
                    kills,
                    killedPmc = killTypes.pmc,
                    killedScav = killTypes.scav,
                    killedBoss = killTypes.boss,
                    deaths = alive ? 0 : 1,
                    damageDealt = 0,
                    damageReceived = 0,
                    accuracy = 0.0,
                    xpEarned,
                    lootValue = 0L
                });

                Plugin.Log.LogInfo($"[ZSlayerHQ]   Player: {player.Profile?.Nickname} — {outcome}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ZSlayerHQ]   Player summary error: {ex.Message}");
            }
        }

        // Collect boss states — use "alive" field (not "killed") to match BossStateEntry model
        var bossList = new List<object>();
        var allPlayers = coopHandler.Players;
        if (allPlayers != null)
        {
            foreach (var kvp in allPlayers)
            {
                var player = kvp.Value;
                if (player == null || !player.IsAI) continue;
                var role = player.Profile?.Info?.Settings?.Role ?? WildSpawnType.marksman;
                if (LocaleUtils.IsBoss(role, out var bossName))
                {
                    var bossAlive = player.HealthController?.IsAlive ?? false;
                    string killedBy = null;
                    if (!bossAlive)
                    {
                        try
                        {
                            var aggressor = LastAggressorField?.GetValue(player) as IPlayer;
                            killedBy = aggressor?.Profile?.Nickname;
                        }
                        catch { /* ignore */ }
                    }
                    bossList.Add(new { name = bossName, alive = bossAlive, killedBy });
                    Plugin.Log.LogInfo($"[ZSlayerHQ]   Boss: {bossName} — alive: {bossAlive}");
                }
            }
        }

        // Send final damage stats before summary
        ReportDamageStats();

        var totalDeaths = humans.Count(p => p.HealthController?.IsAlive == false);
        Plugin.Log.LogInfo($"[ZSlayerHQ] Posting raid-summary: map={_currentMap}, duration={raidDuration}s, players={playerSummaries.Count}, kills={_raidKillCount}, deaths={totalDeaths}, bosses={bossList.Count}");

        _reporter.Post("raid-summary", new
        {
            map = _currentMap,
            raidDuration,
            players = playerSummaries,
            bosses = bossList,
            totalKills = _raidKillCount,
            totalDeaths
        });
    }

    // ══════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════

    private static double GetPlayerHealthPercent(FikaPlayer player)
    {
        try
        {
            var hc = player.HealthController;
            if (hc == null || !hc.IsAlive) return 0.0;

            var health = hc.GetBodyPartHealth(EBodyPart.Common, true);
            if (health.Maximum <= 0) return 0.0;
            return Math.Round(health.Current / health.Maximum, 2);
        }
        catch
        {
            return 0.0;
        }
    }

    private static string GetBodyPartName(EBodyPart part) => part switch
    {
        EBodyPart.Head => "Head",
        EBodyPart.Chest => "Thorax",
        EBodyPart.Stomach => "Stomach",
        EBodyPart.LeftArm => "Left Arm",
        EBodyPart.RightArm => "Right Arm",
        EBodyPart.LeftLeg => "Left Leg",
        EBodyPart.RightLeg => "Right Leg",
        _ => ""
    };

    private static string GetPlayerType(FikaPlayer player)
    {
        if (player == null) return "unknown";

        if (!player.IsObservedAI)
        {
            return player.Side switch
            {
                EPlayerSide.Usec => "pmc",
                EPlayerSide.Bear => "pmc",
                EPlayerSide.Savage => "scav",
                _ => "pmc"
            };
        }

        var role = player.Profile?.Info?.Settings?.Role ?? WildSpawnType.marksman;

        if (LocaleUtils.IsBoss(role, out _))
            return "boss";

        return role switch
        {
            WildSpawnType.pmcBot => "rogue",
            WildSpawnType.exUsec => "rogue",
            WildSpawnType.arenaFighter => "raider",
            WildSpawnType.sectantPriest => "follower",
            WildSpawnType.sectantWarrior => "follower",
            _ => "scav"
        };
    }

    private class CoroutineHelper : MonoBehaviour { }
}
