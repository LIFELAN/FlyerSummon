using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

#pragma warning disable HARMONIZE004 // Analyzer cannot infer targets for a couple of patches; HarmonyX resolves them at runtime.

namespace FlyerSummon;

/// <summary>
/// Harmony patches that let a friendly Flintflame Flyer reuse the vanilla PlayMaker FSM while
/// fighting enemies instead of Hornet.
/// </summary>
[HarmonyPatch]
internal static class AllyPatches
{
    /// <summary>
    /// The vanilla FSM asks its <see cref="AlertRange"/> components whether the <em>hero</em> is in
    /// range. For a friendly flyer we answer with enemies instead, using an explicit physics query so
    /// the result does not depend on the layer collision matrix or the range's line-of-sight settings.
    /// </summary>
    [HarmonyPatch(typeof(AlertRange), nameof(AlertRange.IsHeroInRange))]
    [HarmonyPrefix]
    private static bool AlertRange_IsHeroInRange_Prefix(AlertRange __instance, ref bool __result)
    {
        var ally = __instance.GetComponentInParent<AllyFlyer>();
        if (ally == null)
        {
            return true;
        }

        __result = ally.HasEnemyInRange(__instance);
        return false;
    }

    /// <summary>
    /// Make friendly flyers truly invincible while <c>Ally/AllyInvincible</c> is on. Returning
    /// <see cref="IHitResponder.Response.None"/> also means no "BLOCKED EXPLOSION" / "TOOK DAMAGE"
    /// events fire, so the ally never drops out of its attack loop when its own bomb goes off.
    /// </summary>
    [HarmonyPatch(typeof(HealthManager), nameof(HealthManager.Hit))]
    [HarmonyPrefix]
    private static bool HealthManager_Hit_Prefix(
        HealthManager __instance,
        ref IHitResponder.HitResponse __result)
    {
        if (!FlyerSummonPlugin.AllyInvincible.Value
            || __instance.GetComponentInParent<AllyFlyer>() == null)
        {
            return true;
        }

        __result = IHitResponder.Response.None;
        return false;
    }

    /// <summary>
    /// Silences the friendly flyer's vanilla throw sound/voice. Postfix so the FSM action still
    /// finishes normally (skipping OnEnter would stall the throw state).
    /// </summary>
    [HarmonyPatch(typeof(PlayAudioEventBase), nameof(PlayAudioEventBase.OnEnter))]
    [HarmonyPostfix]
    private static void PlayAudioEvent_OnEnter_Postfix(PlayAudioEventBase __instance, AudioSource ___spawnedAudioSource)
    {
        if (!ShouldMuteAllyAudio(__instance.Owner) || ___spawnedAudioSource == null)
        {
            return;
        }

        ___spawnedAudioSource.Stop();
        UnityEngine.Object.Destroy(___spawnedAudioSource.gameObject);
    }

    [HarmonyPatch(typeof(AudioPlayRandomVoiceFromTableV2), nameof(AudioPlayRandomVoiceFromTableV2.OnEnter))]
    [HarmonyPostfix]
    private static void AudioPlayRandomVoice_OnEnter_Postfix(
        AudioPlayRandomVoiceFromTableV2 __instance,
        AudioSource ___audio)
    {
        if (ShouldMuteAllyAudio(__instance.Owner) && ___audio != null)
        {
            ___audio.Stop();
        }
    }

    private static bool ShouldMuteAllyAudio(GameObject? owner)
    {
        return !FlyerSummonPlugin.ThrowSound.Value
               && owner != null
               && owner.GetComponentInParent<AllyFlyer>() != null;
    }

    /// <summary>
    /// Marks bombs spawned by a friendly flyer. Patching the spawn action is precise: the shared
    /// bomb pool is also used by the vanilla enemy, so we must only touch ally-spawned instances.
    /// </summary>
    [HarmonyPatch(typeof(SpawnObjectFromGlobalPool), nameof(SpawnObjectFromGlobalPool.OnEnter))]
    [HarmonyPostfix]
    private static void SpawnObjectFromGlobalPool_OnEnter_Postfix(SpawnObjectFromGlobalPool __instance)
    {
        var owner = __instance.Owner;
        if (owner == null)
        {
            return;
        }

        var ally = owner.GetComponentInParent<AllyFlyer>();
        if (ally == null)
        {
            return;
        }

        var spawned = __instance.storeObject?.Value;
        if (spawned == null || spawned.GetComponent<FireballProjectile>() == null)
        {
            return;
        }

        ally.OnAllyBombSpawned(spawned);
    }
}
