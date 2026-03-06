using System;
using System.Collections.Generic;

namespace ZSlayerHeadlessTelemetry;

/// <summary>
/// Per-player damage/hit statistics during a raid.
/// Tracks both global totals (backward compat) and per-player breakdowns.
/// Reset on raid start, read at raid end for summary.
/// </summary>
public class PlayerDamageStats
{
    public int DamageDealt;
    public int DamageReceived;
    public int Hits;
    public int Headshots;
    public double LongestShot;
    public int HitsHead, HitsChest, HitsStomach, HitsLeftArm, HitsRightArm, HitsLeftLeg, HitsRightLeg;
}

public static class DamageTracker
{
    private static readonly Dictionary<string, PlayerDamageStats> _playerStats = new();

    // Global totals (backward compat with damage-stats report)
    public static int HitsHead;
    public static int HitsChest;
    public static int HitsStomach;
    public static int HitsLeftArm;
    public static int HitsRightArm;
    public static int HitsLeftLeg;
    public static int HitsRightLeg;

    public static int TotalHits;
    public static int TotalDamageDealt;
    public static int HeadshotCount;
    public static double LongestShot;
    public static double TotalDistance;

    private static PlayerDamageStats GetOrCreate(string profileId)
    {
        if (!_playerStats.TryGetValue(profileId, out var stats))
        {
            stats = new PlayerDamageStats();
            _playerStats[profileId] = stats;
        }
        return stats;
    }

    public static void Reset()
    {
        _playerStats.Clear();
        HitsHead = 0;
        HitsChest = 0;
        HitsStomach = 0;
        HitsLeftArm = 0;
        HitsRightArm = 0;
        HitsLeftLeg = 0;
        HitsRightLeg = 0;
        TotalHits = 0;
        TotalDamageDealt = 0;
        HeadshotCount = 0;
        LongestShot = 0;
        TotalDistance = 0;
    }

    public static void RecordHit(string attackerId, string victimId, int bodyPart, float damage, float distance)
    {
        // Update global stats (existing behavior)
        TotalHits++;
        TotalDamageDealt += (int)damage;

        if (distance > LongestShot)
            LongestShot = Math.Round(distance, 1);
        TotalDistance += distance;

        // EBodyPart enum: Head=0, Chest=1, Stomach=2, LeftArm=3, RightArm=4, LeftLeg=5, RightLeg=6
        switch (bodyPart)
        {
            case 0: HitsHead++; HeadshotCount++; break;
            case 1: HitsChest++; break;
            case 2: HitsStomach++; break;
            case 3: HitsLeftArm++; break;
            case 4: HitsRightArm++; break;
            case 5: HitsLeftLeg++; break;
            case 6: HitsRightLeg++; break;
        }

        // Update attacker's per-player stats
        if (!string.IsNullOrEmpty(attackerId))
        {
            var attacker = GetOrCreate(attackerId);
            attacker.DamageDealt += (int)damage;
            attacker.Hits++;
            if (bodyPart == 0) attacker.Headshots++;
            if (distance > attacker.LongestShot) attacker.LongestShot = Math.Round(distance, 1);
            switch (bodyPart)
            {
                case 0: attacker.HitsHead++; break;
                case 1: attacker.HitsChest++; break;
                case 2: attacker.HitsStomach++; break;
                case 3: attacker.HitsLeftArm++; break;
                case 4: attacker.HitsRightArm++; break;
                case 5: attacker.HitsLeftLeg++; break;
                case 6: attacker.HitsRightLeg++; break;
            }
        }

        // Update victim's per-player stats
        if (!string.IsNullOrEmpty(victimId))
        {
            var victim = GetOrCreate(victimId);
            victim.DamageReceived += (int)damage;
        }
    }

    public static PlayerDamageStats GetPlayerStats(string profileId)
    {
        _playerStats.TryGetValue(profileId, out var stats);
        return stats;
    }

    public static Dictionary<string, PlayerDamageStats> GetAllPlayerStats()
        => new(_playerStats);

    public static double AvgDistance => TotalHits > 0 ? Math.Round(TotalDistance / TotalHits, 1) : 0;

    public static object ToPayload(string sourceId = null)
    {
        // Build per-player payload
        var perPlayer = new Dictionary<string, object>();
        foreach (var kvp in _playerStats)
        {
            var s = kvp.Value;
            perPlayer[kvp.Key] = new
            {
                damageDealt = s.DamageDealt,
                damageReceived = s.DamageReceived,
                hits = s.Hits,
                headshots = s.Headshots,
                longestShot = s.LongestShot,
                bodyParts = new
                {
                    head = s.HitsHead,
                    chest = s.HitsChest,
                    stomach = s.HitsStomach,
                    leftArm = s.HitsLeftArm,
                    rightArm = s.HitsRightArm,
                    leftLeg = s.HitsLeftLeg,
                    rightLeg = s.HitsRightLeg
                }
            };
        }

        return new
        {
            sourceId,
            totalHits = TotalHits,
            totalDamageDealt = TotalDamageDealt,
            headshotCount = HeadshotCount,
            longestShot = LongestShot,
            avgDistance = AvgDistance,
            bodyParts = new
            {
                head = HitsHead,
                chest = HitsChest,
                stomach = HitsStomach,
                leftArm = HitsLeftArm,
                rightArm = HitsRightArm,
                leftLeg = HitsLeftLeg,
                rightLeg = HitsRightLeg
            },
            players = perPlayer
        };
    }
}
