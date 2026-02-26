using System;
using System.Reflection;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace ZSlayerHeadlessTelemetry;

/// <summary>
/// Harmony patch on Player.ApplyDamageInfo to track hits/damage dealt by human players.
/// </summary>
public class OnDamagePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(Player).GetMethod(nameof(Player.ApplyDamageInfo),
            BindingFlags.Public | BindingFlags.Instance);
    }

    [PatchPostfix]
    private static void Postfix(Player __instance, DamageInfoStruct damageInfo, EBodyPart bodyPartType)
    {
        try
        {
            // Only track damage dealt by human players
            var attacker = damageInfo.Player as Player;
            if (attacker == null || attacker.IsAI) return;

            float distance = 0f;
            try
            {
                distance = Vector3.Distance(attacker.Position, __instance.Position);
            }
            catch { /* ignore position errors */ }

            var attackerId = attacker.ProfileId ?? "";
            var victimId = __instance.ProfileId ?? "";
            DamageTracker.RecordHit(attackerId, victimId, (int)bodyPartType, damageInfo.Damage, distance);
        }
        catch { /* never crash the game */ }
    }
}
