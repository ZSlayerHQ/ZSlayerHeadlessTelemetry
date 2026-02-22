<div align="center">

# ZSlayer Headless Telemetry

**Live raid telemetry plugin for SPT / FIKA headless clients**

[![License: MIT](https://img.shields.io/badge/License-MIT-c8aa6e.svg)](LICENSE)
[![SPT](https://img.shields.io/badge/SPT-4.0.x-c8aa6e.svg)]()
[![FIKA](https://img.shields.io/badge/FIKA-Required-4a7c59.svg)]()
[![BepInEx](https://img.shields.io/badge/BepInEx-5.x-blue.svg)]()

---

A BepInEx plugin that runs on FIKA headless clients and streams real-time raid telemetry to the [ZSlayer Command Center](https://github.com/ZSlayerHQ/ZSlayerCommandCenter). Watch raids unfold from your browser — player positions, bot counts, kill events, combat stats, and more.

[Discord](https://discord.gg/ZSlayerHQ) | [YouTube](https://www.youtube.com/@ZSlayerHQ-ImBenCole)

</div>

---

## What Is This?

This is the **companion plugin** for [ZSlayer Command Center](https://github.com/ZSlayerHQ/ZSlayerCommandCenter). It runs inside the FIKA headless client and streams live raid data back to the Command Center server mod, powering the Raid Info tab.

The plugin activates **only on headless clients** — it does nothing on regular game clients. It auto-discovers the server URL from SPT's backend config, so there's zero configuration required.

---

## What It Reports

Every **5 seconds** during a raid:
- **Raid State** — status, map, elapsed time, in-game time of day
- **Players** — all human players with health, level, side, ping
- **Performance** — FPS (current/avg/min-max), frame time, RAM, CPU usage, system info

Every **1 second** (configurable):
- **Positions** — real-time coordinates of all players and bots for the live minimap

Every **10 seconds**:
- **Bot Counts** — scavs, raiders, rogues, bosses (alive/dead with killer attribution)
- **Damage Stats** — total hits, headshots, body part distribution, damage dealt

**Immediately** on event:
- **Kill Events** — killer, victim, weapon, ammo, body part, distance, headshot flag
- **Extractions** — player outcome (survived, MIA, killed), extract point

**At raid end:**
- **Raid Summary** — per-player scoreboard (kills by type, XP earned, outcome), boss states, full combat stats

---

## Installation

1. Download `ZSlayerHeadlessTelemetry.dll` from the [latest release](https://github.com/ZSlayerHQ/ZSlayerHeadlessTelemetry/releases)
2. Place it in your headless client's BepInEx plugins folder:
   ```
   Headless/
   └── BepInEx/
       └── plugins/
           └── ZSlayerHeadlessTelemetry/
               └── ZSlayerHeadlessTelemetry.dll
   ```
3. Ensure [ZSlayer Command Center](https://github.com/ZSlayerHQ/ZSlayerCommandCenter) is installed on your SPT server
4. Start the server, then start the headless client
5. Open the Command Center Raid Info tab — telemetry appears automatically when a raid starts

---

## Architecture

```
┌─────────────────────────────────────┐
│         HEADLESS CLIENT             │
│         (BepInEx Plugin)            │
│                                     │
│  Plugin.cs ─── Entry point          │
│    │  Only activates if headless    │
│    │  Auto-discovers server URL     │
│    ▼                                │
│  RaidEventHooks.cs                  │
│    │  Subscribes to Fika events:    │
│    │  • FikaGameCreatedEvent        │
│    │  • FikaRaidStartedEvent        │
│    │  • FikaGameEndedEvent          │
│    │                                │
│    ├── PeriodicReportLoop (5s)      │
│    │   ├── ReportRaidState()        │
│    │   ├── ReportPlayers()          │
│    │   ├── ReportPerformance()      │
│    │   ├── ReportBots() (10s)       │
│    │   └── ReportDamageStats() (10s)│
│    │                                │
│    ├── PositionReportLoop (1s)      │
│    │   └── ReportPositions()        │
│    │                                │
│    └── Kill handlers (per-player)   │
│        └── OnPlayerDied()           │
│                                     │
│  OnDamagePatch.cs ─── Harmony patch │
│    └── Player.ApplyDamageInfo       │
│                                     │
│  DamageTracker.cs ─── Hit counter   │
│  TelemetryReporter.cs ─── HTTP POST │
└──────────────┬──────────────────────┘
               │ HTTP POST (JSON)
               ▼
┌──────────────────────────────────────┐
│  SPT SERVER                          │
│  ZSlayer Command Center              │
│  POST /zslayer/cc/telemetry/{type}   │
└──────────────────────────────────────┘
```

### Resilience

Each report in the periodic loop is individually try-caught — if one report fails (e.g., a disposed player reference), all other reports still execute. Per-entity safety in player/bot iteration means a single bad entity is skipped rather than crashing the entire report.

---

## Files

| File | Purpose |
|------|---------|
| `Plugin.cs` | BepInEx entry point, headless detection, server URL discovery |
| `RaidEventHooks.cs` | All raid event handling, periodic reporting, kill tracking |
| `TelemetryReporter.cs` | Async HTTP POST queue with retry and SSL bypass |
| `DamageTracker.cs` | Static hit/damage accumulator for combat stats |
| `OnDamagePatch.cs` | Harmony patch on `Player.ApplyDamageInfo` for damage tracking |

---

## Requirements

| Requirement | Version |
|-------------|---------|
| **SPT** | 4.0.x |
| **FIKA** | Latest (hard dependency) |
| **ZSlayer Command Center** | 2.2+ (server mod) |
| **BepInEx** | 5.x (bundled with SPT) |

---

## FAQ

<details>
<summary><strong>Does this run on regular game clients?</strong></summary>

No. The plugin checks `FikaBackendUtils.IsHeadless` on startup and disables itself on non-headless clients. It will never affect player game performance.

</details>

<details>
<summary><strong>Do I need to configure the server URL?</strong></summary>

No. The plugin reads the server URL from SPT's `RequestHandler.Host` automatically. It points to wherever the headless client is configured to connect.

</details>

<details>
<summary><strong>What happens if the server is unreachable?</strong></summary>

Reports are silently dropped. The plugin logs a warning on startup if the initial ping fails, but continues operating. When the server comes back, the next report cycle picks up automatically.

</details>

<details>
<summary><strong>Can I adjust the position update rate?</strong></summary>

Yes. The plugin fetches the configured rate from the Command Center server on raid start. Adjust the map refresh rate slider in the Raid Info tab to control how frequently positions are reported (0.05s to 10s).

</details>

---

## License

[MIT](LICENSE) — Built by [ZSlayerHQ / Ben Cole](https://github.com/ZSlayerHQ)
