using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace FlyerSummon;

/// <summary>
/// Summons a fully functional Flintflame Flyer (燧焰飞工, internal GameObject name "Dock Bomber")
/// anywhere.
///
/// Background: the enemy only exists as a scene object in Deep Docks, and its bomb
/// ("DF Bomb Rock"), sprite atlas ("tk2dcollections_assets_areadocks") and animations
/// ("tk2danimations_assets_areadocks") all live in Deep Docks specific Addressables bundles. If
/// those bundles unload, the throw animation still plays but no bomb appears (the
/// PersonalObjectPool entry is dropped and SpawnObjectFromGlobalPool gets a null prefab).
///
/// The fix: load the Deep Docks library scene with
/// <see cref="SceneReleaseMode.OnlyReleaseSceneOnHandleRelease"/>, freeze it immediately so its
/// CustomSceneManager never runs, clone the enemy into a persistent template, then unload the
/// scene while keeping the Addressables handle. The handle keeps every dependency bundle alive,
/// but the scene is gone from Unity's scene list, so it cannot break scene transitions.
/// </summary>
internal sealed class FlyerSummonController : MonoBehaviour
{
    internal static FlyerSummonController? Instance;

    private static readonly FieldInfo? JournalRecordField =
        typeof(EnemyDeathEffects).GetField("journalRecord", BindingFlags.Instance | BindingFlags.NonPublic);

    private AsyncOperationHandle<SceneInstance> _libraryHandle;
    private GameObject? _template;
    private GameObject? _allyTemplate;
    private bool _loading;
    private bool _preloaded;

    private float _heroAbsentTime;
    private float _heroPresentTime;

    private void Awake()
    {
        Instance = this;
    }

    /// <summary>Starts loading the prefab library so the tool replacement is ready before use.</summary>
    internal void EnsureTemplateLoaded()
    {
        if (_template != null || _loading)
        {
            return;
        }

        if (TryGetActiveSceneEnemy(out var source))
        {
            MakeTemplate(source);
            return;
        }

        StartCoroutine(LoadLibrary());
    }

    internal static void RequestPreload()
    {
        Instance?.EnsureTemplateLoaded();
    }

    private void Update()
    {
        // Preload once we are actually in gameplay so the Cogwork Flier tool has a flyer to throw.
        if (HeroController.instance != null)
        {
            _heroPresentTime += Time.deltaTime;
            if (!_preloaded && _heroPresentTime > 5f)
            {
                _preloaded = true;
                EnsureTemplateLoaded();
            }
        }
        else
        {
            _heroPresentTime = 0f;
        }
        // When the hero is gone for a while we are almost certainly in a menu or between saves;
        // drop persisted flyers so they don't leak into the next save.
        if (HeroController.instance == null)
        {
            _heroAbsentTime += Time.deltaTime;
            if (_heroAbsentTime > 3f && AllyFlyer.AnyActive)
            {
                AllyFlyer.Clear();
            }
        }
        else
        {
            _heroAbsentTime = 0f;
        }

        var clearKey = FlyerSummonPlugin.ClearAlliesHotkey.Value;
        if (clearKey != KeyCode.None && Input.GetKeyDown(clearKey))
        {
            AllyFlyer.Clear();
            return;
        }

        var friendlyKey = FlyerSummonPlugin.FriendlyHotkey.Value;
        if (friendlyKey != KeyCode.None && Input.GetKeyDown(friendlyKey))
        {
            OnHotkey(friendly: true);
            return;
        }

        var key = FlyerSummonPlugin.Hotkey.Value;
        if (key != KeyCode.None && Input.GetKeyDown(key))
        {
            OnHotkey(friendly: false);
        }
    }

    private void OnHotkey(bool friendly)
    {
        if (_template != null)
        {
            Spawn(friendly);
            return;
        }

        // If the player is already in a Deep Docks scene the enemy and its bundles are loaded, so
        // there is no need to load the library.
        if (TryGetActiveSceneEnemy(out var source))
        {
            MakeTemplate(source);
            Spawn(friendly);
            return;
        }

        if (_loading)
        {
            FlyerSummonPlugin.LogInfo("Flyer template is still loading…");
            return;
        }

        StartCoroutine(LoadThenSummon(friendly));
    }

    private IEnumerator LoadThenSummon(bool friendly)
    {
        yield return LoadLibrary();

        if (_template != null)
        {
            Spawn(friendly);
        }
    }

    private IEnumerator LoadLibrary()
    {
        _loading = true;
        var address = FlyerSummonPlugin.SceneAddress;
        FlyerSummonPlugin.LogInfo($"Loading library scene '{address}'…");

        // activateOnLoad: true is required: a scene loaded with allowSceneActivation = false cannot
        // be traversed, does not fire SceneManager.sceneLoaded (which other mods rely on), and its
        // objects are not returned by FindObjectsOfTypeAll.
        var op = Addressables.LoadSceneAsync(
            address,
            LoadSceneMode.Additive,
            SceneReleaseMode.OnlyReleaseSceneOnHandleRelease,
            activateOnLoad: true);
        yield return op;

        _loading = false;

        if (op.Status != AsyncOperationStatus.Succeeded)
        {
            FlyerSummonPlugin.LogError($"Failed to load library scene '{address}': {op.OperationException}");
            yield break;
        }

        _libraryHandle = op;

        var scene = ResolveScene(address, op.Result.Scene);
        if (!scene.IsValid())
        {
            FlyerSummonPlugin.LogError($"Library scene '{address}' loaded but could not be resolved; unloading it.");
            yield return Addressables.UnloadSceneAsync(op);
            yield break;
        }

        var name = FlyerSummonPlugin.EnemyObjectName;
        GameObject? source = null;

        try
        {
            FlyerSummonPlugin.LogInfo(
                $"Library scene '{scene.name}' loaded: rootCount={scene.rootCount}, path='{scene.path}'.");

            // Freeze the scene before its Start/Update phase runs, so its CustomSceneManager cannot
            // change music, environment or camera. Awake/OnEnable have already run by now; that is
            // unavoidable when activating a scene.
            DisableScene(scene);
            source = FindObjectByName(scene, name);
        }
        catch (Exception e)
        {
            FlyerSummonPlugin.LogError("Error while inspecting library scene: " + e);
        }

        if (source != null)
        {
            // Clone the enemy while the scene is still loaded: unloading the scene destroys `source`.
            MakeTemplate(source);
        }
        else
        {
            LogSceneDiagnostics(scene, name);
        }

        // The scene must never be left loaded: a lingering additive scene breaks the game's own
        // scene transitions. The Addressables handle is kept, so the dependency bundles stay loaded.
        FlyerSummonPlugin.LogInfo($"Unloading library scene '{scene.name}'.");
        yield return SceneManager.UnloadSceneAsync(scene);

        if (_template == null)
        {
            FlyerSummonPlugin.LogError($"'{name}' was not found in library scene '{scene.name}'.");
            yield break;
        }

        FlyerSummonPlugin.LogInfo($"Template '{name}' extracted from '{scene.name}'.");
    }

    private static void DisableScene(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.activeSelf)
            {
                root.SetActive(false);
            }
        }
    }

    private static void LogSceneDiagnostics(Scene scene, string wantedName)
    {
        try
        {
            var roots = scene.GetRootGameObjects();
            FlyerSummonPlugin.LogInfo($"Library scene '{scene.name}' rootCount={roots.Length}.");

            var shown = Math.Min(roots.Length, 30);
            for (var i = 0; i < shown; i++)
            {
                FlyerSummonPlugin.LogInfo($"  root[{i}] = '{roots[i].name}'");
            }
        }
        catch (Exception e)
        {
            FlyerSummonPlugin.LogError("Failed to list library scene roots: " + e.Message);
        }

        // Global scan: list every loaded GameObject whose name looks relevant, plus its scene.
        try
        {
            var hits = new System.Collections.Generic.List<string>();
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || string.IsNullOrEmpty(go.name))
                {
                    continue;
                }

                if (go.name.IndexOf("Bomber", StringComparison.OrdinalIgnoreCase) >= 0
                    || go.name.IndexOf("Flyer", StringComparison.OrdinalIgnoreCase) >= 0
                    || go.name.Equals(wantedName, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add($"{go.name} [scene={go.scene.name}, active={go.activeInHierarchy}]");
                    if (hits.Count >= 40)
                    {
                        break;
                    }
                }
            }

            FlyerSummonPlugin.LogInfo($"Global scan for 'Bomber'/'Flyer' ({hits.Count}): {string.Join("; ", hits)}");
        }
        catch (Exception e)
        {
            FlyerSummonPlugin.LogError("Global scan failed: " + e.Message);
        }
    }

    private void MakeTemplate(GameObject source)
    {
        var wasActive = source.activeSelf;

        // Clone while inactive so the enemy's Awake/FSM does not run on the template.
        source.SetActive(false);
        var template = Instantiate(source);
        source.SetActive(wasActive);

        template.name = source.name + " (Template)";
        DontDestroyOnLoad(template);
        template.SetActive(false);

        // The original is tracked by the save file through PersistentBoolItem (scene + id). Strip it
        // so spawned copies never read "already dead" or write back to the original's state.
        var persistent = template.GetComponent<PersistentBoolItem>();
        if (persistent != null)
        {
            Destroy(persistent);
        }

        _template = template;
        FlyerSummonPlugin.LogInfo($"Template '{template.name}' cached.");

        BuildAllyAssets(template);
    }

    /// <summary>
    /// Builds the friendly prefab and icon used when the Cogwork Flier tool is replaced.
    /// </summary>
    private void BuildAllyAssets(GameObject template)
    {
        if (_allyTemplate != null)
        {
            return;
        }

        var ally = Instantiate(template);
        ally.name = template.name + " (Ally Template)";
        DontDestroyOnLoad(ally);
        ally.SetActive(false);
        ally.AddComponent<AllyFlyer>();

        _allyTemplate = ally;
        CogworkFlierReplacement.AllyPrefab = ally;
        CogworkFlierReplacement.AllyIcon ??= ExtractJournalIcon(template);

        FlyerSummonPlugin.LogInfo(
            "Ally template ready for the tool replacement "
            + $"(icon={(CogworkFlierReplacement.AllyIcon != null ? "ok" : "missing")}).");
    }

    private static Sprite? ExtractJournalIcon(GameObject template)
    {
        if (JournalRecordField == null)
        {
            return null;
        }

        var effects = template.GetComponent<EnemyDeathEffects>();
        if (effects == null)
        {
            return null;
        }

        var record = JournalRecordField.GetValue(effects) as EnemyJournalRecord;
        return record != null ? record.IconSprite : null;
    }

    private bool TryGetActiveSceneEnemy([NotNullWhen(true)] out GameObject? source)
    {
        var name = FlyerSummonPlugin.EnemyObjectName;
        source = FindObjectByName(SceneManager.GetActiveScene(), name);
        return source != null;
    }

    private static GameObject? FindObjectByName(Scene scene, string name)
    {
        if (!scene.IsValid())
        {
            return null;
        }

        try
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (Matches(t.name, name))
                    {
                        return t.gameObject;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Scene not traversable; fall through to the scan.
        }

        // Fallback: any loaded object in this scene whose name matches, plus a looser
        // "contains" match as a safety net for unexpected name decoration.
        GameObject? loose = null;
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
        {
            if (go == null || go.scene != scene)
            {
                continue;
            }

            if (Matches(go.name, name))
            {
                return go;
            }

            if (loose == null && go.name.IndexOf("Bomber", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                loose = go;
            }
        }

        return loose;
    }

    private static bool Matches(string candidate, string wanted)
    {
        return string.Equals(candidate.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private void Spawn(bool friendly)
    {
        var hero = HeroController.instance;
        if (hero == null || _template == null)
        {
            FlyerSummonPlugin.LogInfo("Cannot summon: no hero or no template.");
            return;
        }

        var pos = FlyerSummonPlugin.GetSpawnPosition(hero.transform.position);

        var clone = Instantiate(_template, pos, Quaternion.identity);
        clone.name = FlyerSummonPlugin.EnemyObjectName + (friendly ? " (Ally)" : " (Summoned)");

        if (friendly && FlyerSummonPlugin.PersistAcrossScenes.Value)
        {
            // Survive scene transitions; the Addressables handle keeps the needed bundles loaded.
            DontDestroyOnLoad(clone);
        }
        else
        {
            // Keep summoned enemies in the gameplay scene so they unload with it.
            SceneManager.MoveGameObjectToScene(clone, SceneManager.GetActiveScene());
        }

        if (friendly)
        {
            var ally = clone.AddComponent<AllyFlyer>();
            ally.Initialize();
        }

        clone.SetActive(true);

        FlyerSummonPlugin.LogInfo($"Summoned '{clone.name}' at {pos}.");
    }


    private static Scene ResolveScene(string address, Scene scene)
    {
        if (scene.IsValid())
        {
            return scene;
        }

        var name = Path.GetFileNameWithoutExtension(address);
        var byName = SceneManager.GetSceneByName(name);
        if (byName.IsValid())
        {
            return byName;
        }

        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var s = SceneManager.GetSceneAt(i);
            if (s.path != null && s.path.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return s;
            }
        }

        return default;
    }

    private void OnDestroy()
    {
        _template = null;

        if (_libraryHandle.IsValid())
        {
            try
            {
                Addressables.Release(_libraryHandle);
            }
            catch (Exception e)
            {
                FlyerSummonPlugin.LogError("Failed to release library handle: " + e.Message);
            }
        }
    }
}
