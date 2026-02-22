using System;
using System.Collections.Generic;

namespace ZSlayerHeadlessTelemetry;

/// <summary>
/// Static tracker for damage/hit statistics during a raid.
/// Reset on raid start, read at raid end for summary.
/// </summary>
public static class DamageTracker
{
    // Body part hit counts
    public static int HitsHead;
    public static int HitsChest;
    public static int HitsStomach;
    public static int HitsLeftArm;
    public static int HitsRightArm;
    public static int HitsLeftLeg;
    public static int HitsRightLeg;

    // Aggregate stats
    public static int TotalHits;
    public static int TotalDamageDealt;
    public static int HeadshotCount;
    public static double LongestShot;
    public static double TotalDistance;

    public static void Reset()
    {
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

    public static void RecordHit(int bodyPart, float damage, float distance)
    {
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
    }

    public static double AvgDistance => TotalHits > 0 ? Math.Round(TotalDistance / TotalHits, 1) : 0;

    public static object ToPayload()
    {
        return new
        {
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
            }
        };
    }
}
