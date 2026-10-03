using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FlyerSummon;

[BepInAutoPlugin(id: "io.github.lifelan.flyersummon")]
public partial class FlyerSummonPlugin : BaseUnityPlugin
{
    internal static FlyerSummonPlugin Instance { get; private set; } = null!;

    // Fixed, tested values (not configurable).
    internal const string SceneAddress = "Scenes/Dock_02";
    internal const string EnemyObjectName = "Dock Bomber";
    internal const float SpawnOffsetX = 3f;
    internal const float SpawnOffsetY = 1.5f;
    internal const float SpawnRadius = 3f;
    internal const float EnemySearchRadius = 25f;
    internal const float FollowDistance = 12f;
    internal const float WarpDistance = 25f;
    internal const float AimSpread = 1f;
    internal const float OrbitRadius = 6f;

    internal static ConfigEntry<KeyCode> Hotkey = null!;
    internal static ConfigEntry<KeyCode> FriendlyHotkey = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    // Friendly companion settings.
    internal static ConfigEntry<int> MaxCount = null!;
    internal static ConfigEntry<int> BombDamage = null!;
    internal static ConfigEntry<bool> AwardJournalKill = null!;
    internal static ConfigEntry<float> BombVolume = null!;
    internal static ConfigEntry<float> BombEffectScale = null!;
    internal static ConfigEntry<bool> ThrowSound = null!;
    internal static ConfigEntry<float> BodyScale = null!;
    internal static ConfigEntry<bool> AllyInvincible = null!;
    internal static ConfigEntry<bool> PersistAcrossScenes = null!;
    internal static ConfigEntry<bool> ReplaceCogworkFlier = null!;
    internal static ConfigEntry<bool> AutoDespawnAfterThrows = null!;
    internal static ConfigEntry<int> AutoDespawnThrowCount = null!;
    internal static ConfigEntry<KeyCode> ClearAlliesHotkey = null!;

    private Harmony _harmony = null!;
    private string? _configPath;
    private DateTime _configWriteTime;

    private void Awake()
    {
        Instance = this;

        Hotkey = Config.Bind(
            "General",
            "Hotkey",
            KeyCode.F8,
            new ConfigDescription(
                "Key that summons a Flintflame Flyer next to Hornet. Click the button and press a key to rebind.",
                null,
                new ConfigurationManagerAttributes { CustomHotkeyDrawer = DrawHotkeyField }));

        FriendlyHotkey = Config.Bind(
            "General",
            "FriendlyHotkey",
            KeyCode.F9,
            new ConfigDescription(
                "Key that summons a friendly Flintflame Flyer that attacks enemies and never harms Hornet. Click the button and press a key to rebind.",
                null,
                new ConfigurationManagerAttributes { CustomHotkeyDrawer = DrawHotkeyField }));

        DebugLogging = Config.Bind(
            "General",
            "DebugLogging",
            false,
            "Log extra debugging information to the BepInEx console/log.");

        BombDamage = Config.Bind(
            "Ally",
            "BombDamage",
            15,
            new ConfigDescription(
                "Damage the friendly Flyer's bomb explosion deals to enemies (the vanilla value is 300).",
                new AcceptableValueRange<int>(0, 1000),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        AwardJournalKill = Config.Bind(
            "Ally",
            "AwardJournalKill",
            false,
            "If true, enemies killed by a friendly Flyer count toward the Hunter's Journal.");

        BombVolume = Config.Bind(
            "Ally",
            "BombVolume",
            0.1f,
            new ConfigDescription(
                "Volume multiplier for friendly Flyer bombs (flight loop + explosion). 0 mutes them. Only affects friendly bombs.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        BombEffectScale = Config.Bind(
            "Ally",
            "BombEffectScale",
            1f,
            new ConfigDescription(
                "Size of the friendly bomb explosion's visuals (1 = vanilla size, 0 = invisible). The damage area is kept the same.",
                new AcceptableValueRange<float>(0f, 1f),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        ThrowSound = Config.Bind(
            "Ally",
            "ThrowSound",
            false,
            "If true, friendly Flyers play their vanilla throw sound/voice. Off keeps throwing silent.");

        BodyScale = Config.Bind(
            "Ally",
            "BodyScale",
            1f,
            new ConfigDescription(
                "Visual scale of the friendly Flyer itself (also scales its hitbox and ranges).",
                new AcceptableValueRange<float>(0.2f, 2f),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        AllyInvincible = Config.Bind(
            "Ally",
            "AllyInvincible",
            true,
            "If true, friendly Flyers are immune to all damage coming from Hornet (nail, tools, skills, explosions).");

        MaxCount = Config.Bind(
            "Ally",
            "MaxCount",
            3,
            new ConfigDescription(
                "Maximum number of friendly Flyers alive at once. Summoning beyond this still works and replaces the oldest flyer.",
                new AcceptableValueRange<int>(1, 20),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        PersistAcrossScenes = Config.Bind(
            "Ally",
            "PersistAcrossScenes",
            true,
            "If true, friendly Flyers survive scene transitions and teleport back to Hornet in the new scene.");

        ReplaceCogworkFlier = Config.Bind(
            "Tool",
            "ReplaceCogworkFlier",
            true,
            "If true, the Cogwork Flier tool throws a friendly Flintflame Flyer instead of a cog, and its icons are replaced.");

        AutoDespawnAfterThrows = Config.Bind(
            "Ally",
            "AutoDespawnAfterThrows",
            false,
            "If true, a friendly Flyer disappears after throwing the configured number of bombs. If false, it stays until cleared with the hotkey.");

        AutoDespawnThrowCount = Config.Bind(
            "Ally",
            "AutoDespawnThrowCount",
            5,
            new ConfigDescription(
                "Number of bombs a friendly Flyer throws before disappearing (only used when AutoDespawnAfterThrows is true).",
                new AcceptableValueRange<int>(1, 100),
                new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));

        ClearAlliesHotkey = Config.Bind(
            "General",
            "ClearAlliesHotkey",
            KeyCode.F10,
            new ConfigDescription(
                "Key that removes every friendly Flyer that is currently summoned. Click the button and press a key to rebind.",
                null,
                new ConfigurationManagerAttributes { CustomHotkeyDrawer = DrawHotkeyField }));

        _harmony = new Harmony(Info.Metadata.GUID);
        _harmony.PatchAll(typeof(AllyPatches));
        _harmony.PatchAll(typeof(CogworkFlierReplacement));

        // Prefer the bundled custom icon over the journal icon.
        CogworkFlierReplacement.AllyIcon = LoadEmbeddedIcon() ?? CogworkFlierReplacement.AllyIcon;

        _configPath = Config.ConfigFilePath;
        _configWriteTime = File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : DateTime.MinValue;

        gameObject.AddComponent<FlyerSummonController>();

        Logger.LogInfo(
            $"{Info.Metadata.Name} {Info.Metadata.Version} loaded. Enemy hotkey: {Hotkey.Value}, friendly hotkey: {FriendlyHotkey.Value}");
    }

    /// <summary>Loads the custom Cogwork Flier tool icon embedded in this assembly.</summary>
    internal static Sprite? LoadEmbeddedIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("FlyerSummon.cogworkflyer_icon.png");
            if (stream == null)
            {
                LogError("Embedded tool icon was not found.");
                return null;
            }

            var bytes = new byte[stream.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count <= 0)
                {
                    break;
                }

                read += count;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                LogError("Failed to decode the embedded tool icon.");
                return null;
            }

            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.hideFlags = HideFlags.HideAndDontSave;

            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.name = "FlyerSummon Cogwork Icon";
            sprite.hideFlags = HideFlags.HideAndDontSave;

            LogInfo("Loaded embedded Cogwork Flier tool icon.");
            return sprite;        }
        catch (Exception e)
        {
            LogError("Failed to load the embedded tool icon: " + e);
            return null;
        }
    }

    /// <summary>
    /// Watches the config file so edits made outside ConfigurationManager also apply without a restart.
    /// </summary>
    private void Update()
    {
        if (_configPath == null || Time.frameCount % 60 != 0)
        {
            return;
        }

        try
        {
            var writeTime = File.GetLastWriteTimeUtc(_configPath);
            if (writeTime != _configWriteTime)
            {
                _configWriteTime = writeTime;
                Config.Reload();
                LogInfo("Config hot-reloaded from disk.");
            }
        }
        catch (Exception e)
        {
            LogError("Config hot-reload failed: " + e.Message);
        }
    }

    /// <summary>Randomised spawn point around Hornet so multiple summons do not stack.</summary>
    internal static Vector3 GetSpawnPosition(Vector3 heroPos)
    {
        var pos = new Vector3(
            heroPos.x + SpawnOffsetX,
            heroPos.y + SpawnOffsetY,
            heroPos.z);

        var radius = SpawnRadius;
        if (radius > 0f)
        {
            var jitter = UnityEngine.Random.insideUnitCircle * radius;
            pos.x += jitter.x;
            pos.y += jitter.y;
        }

        return pos;
    }

    private static void DrawHotkeyField(ConfigEntryBase setting, ref bool isEditing)
    {
        if (setting is not ConfigEntry<KeyCode> entry)
        {
            return;
        }

        if (isEditing)
        {
            // Stop the key press from leaking into the search box or another field.
            GUIUtility.keyboardControl = -1;

            GUILayout.Label("Press any key (Esc to cancel)", GUILayout.ExpandWidth(true));

            var current = Event.current;
            if (current != null && current.isKey && current.type == EventType.KeyDown)
            {
                if (current.keyCode == KeyCode.Escape)
                {
                    isEditing = false;
                }
                else if (!IsModifierKey(current.keyCode))
                {
                    entry.Value = current.keyCode;
                    isEditing = false;
                }

                current.Use();
            }
        }
        else if (GUILayout.Button(entry.Value.ToString(), GUILayout.ExpandWidth(true)))
        {
            isEditing = true;
        }
    }

    private static bool IsModifierKey(KeyCode key)
    {
        return key is KeyCode.LeftShift or KeyCode.RightShift
            or KeyCode.LeftControl or KeyCode.RightControl
            or KeyCode.LeftAlt or KeyCode.RightAlt
            or KeyCode.LeftCommand or KeyCode.RightCommand
            or KeyCode.LeftWindows or KeyCode.RightWindows;
    }

    internal static void Log(string message)
    {
        if (DebugLogging is { Value: true })
        {
            Instance.Logger.LogInfo(message);
        }
    }

    internal static void LogInfo(string message)
    {
        Instance.Logger.LogInfo(message);
    }

    internal static void LogError(string message)
    {
        Instance.Logger.LogError(message);
    }
}
