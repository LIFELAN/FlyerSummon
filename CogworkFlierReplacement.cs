using System;
using HarmonyLib;
using UnityEngine;

#pragma warning disable HARMONIZE004 // Analyzer cannot infer targets for a couple of patches; HarmonyX resolves them at runtime.

namespace FlyerSummon;

/// <summary>
/// Replaces the <c>Cogwork Flier</c> tool (齿轮蜂) with the friendly Flintflame Flyer:
/// <list type="bullet">
/// <item>its thrown projectile becomes our friendly flyer (and respects <c>Ally/MaxCount</c>);</item>
/// <item>its inventory/HUD/popup icon becomes the Dock Bomber's journal icon, rescaled to the
/// original cog icon's world size so it fits the HUD.</item>
/// </list>
/// The prefab and icon are prepared by <see cref="FlyerSummonController"/> once the Deep Docks
/// library has been loaded.
/// </summary>
[HarmonyPatch]
internal static class CogworkFlierReplacement
{
    /// <summary>Exact tool name; "Cogwork Saw" must NOT match.</summary>
    private const string ToolName = "Cogwork Flier";

    internal static GameObject? AllyPrefab;
    internal static Sprite? AllyIcon;

    private static Sprite? _hudIcon;
    private static Sprite? _inventoryIcon;
    private static Sprite? _popupIcon;

    private static bool IsCogwork(ToolItem? item)
    {
        return item != null
               && string.Equals(item.name, ToolName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the sprite is one of the replacement icons (used to skip HUD tinting).</summary>
    internal static bool IsAllyIcon(Sprite? sprite)
    {
        return sprite != null
               && (sprite == AllyIcon || sprite == _hudIcon || sprite == _inventoryIcon || sprite == _popupIcon);
    }

    /// <summary>Keep the custom icon in its original colours instead of the HUD's active/inactive tint.</summary>
    [HarmonyPatch(typeof(ToolHudIcon), "SetIconColour")]
    [HarmonyPrefix]
    private static void ToolHudIcon_SetIconColour_Prefix(SpriteRenderer icon, ref Color color)
    {
        if (icon != null && IsAllyIcon(icon.sprite))
        {
            color = Color.white;
        }
    }

    [HarmonyPatch(typeof(ToolItemBasic), "Usage", MethodType.Getter)]
    [HarmonyPostfix]
    private static void Usage_Postfix(ToolItemBasic __instance, ref ToolItem.UsageOptions __result)
    {
        if (!FlyerSummonPlugin.ReplaceCogworkFlier.Value || !IsCogwork(__instance))
        {
            return;
        }

        if (AllyPrefab == null)
        {
            // First use before the library finished loading: fall back to the vanilla cog for now,
            // and make sure the friendly prefab is ready for the next throw.
            FlyerSummonController.RequestPreload();
            return;
        }

        __result.ThrowPrefab = AllyPrefab;
    }

    [HarmonyPatch(typeof(ToolItemBasic), nameof(ToolItemBasic.GetInventorySprite))]
    [HarmonyPostfix]
    private static void GetInventorySprite_Postfix(ToolItemBasic __instance, ref Sprite __result)
    {
        if (!FlyerSummonPlugin.ReplaceCogworkFlier.Value || AllyIcon == null || !IsCogwork(__instance))
        {
            return;
        }

        __result = _inventoryIcon ??= Rescale(__result, AllyIcon);
    }

    [HarmonyPatch(typeof(ToolItemBasic), nameof(ToolItemBasic.GetHudSprite))]
    [HarmonyPostfix]
    private static void GetHudSprite_Postfix(ToolItemBasic __instance, ref Sprite __result)
    {
        if (!FlyerSummonPlugin.ReplaceCogworkFlier.Value || AllyIcon == null || !IsCogwork(__instance))
        {
            return;
        }

        __result = _hudIcon ??= Rescale(__result, AllyIcon);
    }

    [HarmonyPatch(typeof(ToolItemBasic), nameof(ToolItemBasic.GetPopupIcon))]
    [HarmonyPostfix]
    private static void GetPopupIcon_Postfix(ToolItemBasic __instance, ref Sprite __result)
    {
        if (!FlyerSummonPlugin.ReplaceCogworkFlier.Value || AllyIcon == null || !IsCogwork(__instance))
        {
            return;
        }

        __result = _popupIcon ??= Rescale(__result, AllyIcon);
    }

    /// <summary>
    /// Rebuilds <paramref name="source"/> (the flyer journal icon) with the same world size as
    /// <paramref name="original"/> (the vanilla cog icon) so it fits the HUD slot.
    /// </summary>
    private static Sprite Rescale(Sprite original, Sprite source)
    {
        try
        {
            var rect = source.textureRect;
            var originalWorldWidth = original.rect.width / original.pixelsPerUnit;
            if (originalWorldWidth <= 0.0001f || rect.width <= 0.0001f)
            {
                return source;
            }

            var pixelsPerUnit = rect.width / originalWorldWidth;
            var pivot = new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height);
            return Sprite.Create(source.texture, rect, pivot, pixelsPerUnit);
        }
        catch (Exception e)
        {
            FlyerSummonPlugin.LogError("Failed to rescale tool icon: " + e.Message);
            return source;
        }
    }
}

#pragma warning restore HARMONIZE004
