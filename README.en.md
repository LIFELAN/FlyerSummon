# FlyerSummon

A Hollow Knight: Silksong (BepInEx) mod that lets you summon a fully functional
**Flintflame Flyer (燧焰飞工)** — internal GameObject name `Dock Bomber` — next to Hornet, in **any**
area, with a hotkey (default **F8**).

## Why this is not just "spawn a prefab"

The Flintflame Flyer is awkward to summon outside Deep Docks:

- It only exists as a **scene object** inside Deep Docks (`dock_02` / `dock_03c`); there is no
  standalone addressable prefab for it.
- Its bomb (`DF Bomb Rock`), sprite atlas (`tk2dcollections_assets_areadocks`) and animations
  (`tk2danimations_assets_areadocks`) live in **Deep Docks specific Addressables bundles**.
- When you leave Deep Docks those bundles unload. The enemy still plays its throw animation, but
  the `PersonalObjectPool` drops the null bomb entry and `SpawnObjectFromGlobalPool` silently
  spawns nothing — so it deals no damage. That is the "Deep Docks restriction".

## How it works

On the first summon the mod:

1. Loads a small Deep Docks scene via Addressables with
   `SceneReleaseMode.OnlyReleaseSceneOnHandleRelease` and `activateOnLoad: true` (a scene loaded
   with `allowSceneActivation = false` cannot be traversed and does not fire `sceneLoaded`).
2. Clones `Dock Bomber` into a hidden, inactive `DontDestroyOnLoad` template.
3. **Unloads the scene's GameObjects** with `SceneManager.UnloadSceneAsync`, so the scene never
   appears in Unity's scene list and cannot interfere with the game's own scene transitions.
4. **Keeps the Addressables handle alive**, which keeps every dependency bundle (bomb, atlas,
   animations, …) loaded for the whole session.

Every later hotkey press just clones the template at Hornet's position. Spawned enemies are moved
into the gameplay scene so they unload normally, and their `PersistentBoolItem` is stripped so they
never touch the original's save state.

## Configuration

Generated at `BepInEx/config/io.github.lifelan.flyersummon.cfg`:

| Key | Default | Meaning |
| --- | --- | --- |
| `General/Hotkey` | `F8` | Summon key (`KeyCode` name). |
| `General/SceneAddress` | `Scenes/Dock_02` | Addressables address of the Deep Docks library scene. If loading fails, try `scenes/dock_02`. |
| `General/EnemyObjectName` | `Dock Bomber` | Enemy GameObject cloned out of the library scene. |
| `Summon/SpawnOffsetX` | `3` | Horizontal spawn offset from Hornet. |
| `Summon/SpawnOffsetY` | `1.5` | Vertical spawn offset from Hornet. |
| `General/DebugLogging` | `false` | Extra logging. |

## Building

```sh
dotnet build -c Release
```

The build copies `FlyerSummon.dll` into `BepInEx/plugins/lifelan-FlyerSummon/` and produces a
Thunderstore package in `thunderstore/dist/` (paths configured in `SilksongPath.props`).
