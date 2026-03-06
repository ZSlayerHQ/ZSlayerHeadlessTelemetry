using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using BepInEx;
using BepInEx.Logging;
using Fika.Core.Main.Utils;
using SPT.Common.Http;
using UnityEngine;

namespace ZSlayerHeadlessTelemetry;

[BepInPlugin("com.zslayerhq.headlesstelemetry", "ZSlayer Headless Telemetry", "1.4.0")]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static TelemetryReporter Reporter;
    internal static string SourceId = "";
    private RaidEventHooks _hooks;
    private LogStreamService _logStream;
    private Coroutine _idleFlushCoroutine;

    private void Awake()
    {
        Log = Logger;

        // Only activate on headless clients
        if (!FikaBackendUtils.IsHeadless)
        {
            Log.LogInfo("[ZSlayerHQ] Not headless — telemetry disabled");
            enabled = false;
            return;
        }

        // Extract source ID (headless profile ID)
        SourceId = RequestHandler.SessionId ?? "";
        Log.LogInfo($"[ZSlayerHQ] Source ID: {SourceId}");

        // Build the telemetry URL from SPT's backend URL
        var baseUrl = RequestHandler.Host.TrimEnd('/');
        var telemetryUrl = $"{baseUrl}/zslayer/cc/telemetry";

        Log.LogInfo($"[ZSlayerHQ] Headless telemetry active — posting to {telemetryUrl}");

        Reporter = new TelemetryReporter(telemetryUrl);

        // Register log listener for console streaming
        _logStream = new LogStreamService();
        BepInEx.Logging.Logger.Listeners.Add(_logStream);

        _hooks = new RaidEventHooks(Reporter, _logStream, SourceId);
        _hooks.Subscribe();

        // Enable Harmony patches
        new OnDamagePatch().Enable();

        // Report versions to server
        var telemetryVersion = Info.Metadata.Version.ToString();
        var fikaVersion = typeof(FikaBackendUtils).Assembly.GetName().Version?.ToString() ?? "?";
        Log.LogInfo($"[ZSlayerHQ] Versions — Telemetry: {telemetryVersion}, Fika.Core: {fikaVersion}");

        var hostname = Dns.GetHostName();
        var ip = ResolveLocalIp();
        Reporter.Post("hello", new
        {
            telemetryVersion,
            fikaClientVersion = fikaVersion,
            sourceId = SourceId,
            hostname,
            ip
        });

        // Async ping to verify server is reachable
        Reporter.PingAsync();

        // Start idle console flush
        _idleFlushCoroutine = StartCoroutine(IdleConsoleFlushLoop());
    }

    private IEnumerator IdleConsoleFlushLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(10f);
            // Only flush when NOT in raid (during raid, RaidEventHooks flushes every 5s)
            if (_hooks != null && !_hooks.IsInRaid)
            {
                FlushConsoleBuffer();
            }
        }
    }

    internal void FlushConsoleBuffer()
    {
        if (_logStream == null || Reporter == null) return;
        var entries = _logStream.DrainBuffer();
        if (entries.Count > 0)
        {
            Reporter.Post("console", new { sourceId = SourceId, entries });
        }
    }

    private static string ResolveLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            var endpoint = socket.LocalEndPoint as IPEndPoint;
            return endpoint?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private void OnDestroy()
    {
        if (_idleFlushCoroutine != null)
            StopCoroutine(_idleFlushCoroutine);

        _hooks?.Unsubscribe();
        Reporter?.Dispose();

        if (_logStream != null)
        {
            try { BepInEx.Logging.Logger.Listeners.Remove(_logStream); }
            catch { /* best effort */ }
        }
    }
}
