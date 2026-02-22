using BepInEx;
using BepInEx.Logging;
using Fika.Core.Main.Utils;
using SPT.Common.Http;

namespace ZSlayerHeadlessTelemetry;

[BepInPlugin("com.zslayerhq.headlesstelemetry", "ZSlayer Headless Telemetry", "1.0.0")]
[BepInDependency("com.fika.core", BepInDependency.DependencyFlags.HardDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log;
    internal static TelemetryReporter Reporter;
    private RaidEventHooks _hooks;

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

        // Build the telemetry URL from SPT's backend URL
        var baseUrl = RequestHandler.Host.TrimEnd('/');
        var telemetryUrl = $"{baseUrl}/zslayer/cc/telemetry";

        Log.LogInfo($"[ZSlayerHQ] Headless telemetry active — posting to {telemetryUrl}");

        Reporter = new TelemetryReporter(telemetryUrl);
        _hooks = new RaidEventHooks(Reporter);
        _hooks.Subscribe();

        // Enable Harmony patches
        new OnDamagePatch().Enable();

        // Report versions to server
        var telemetryVersion = Info.Metadata.Version.ToString();
        var fikaVersion = typeof(FikaBackendUtils).Assembly.GetName().Version?.ToString() ?? "?";
        Log.LogInfo($"[ZSlayerHQ] Versions — Telemetry: {telemetryVersion}, Fika.Core: {fikaVersion}");
        Reporter.Post("hello", new { telemetryVersion, fikaClientVersion = fikaVersion });

        // Async ping to verify server is reachable
        Reporter.PingAsync();
    }

    private void OnDestroy()
    {
        _hooks?.Unsubscribe();
        Reporter?.Dispose();
    }
}
