# FlyerSummon

**English** | [简体中文](README.md)

A **Hollow Knight: Silksong** BepInEx mod that turns the Deep Docks enemy
**Flintflame Flyer** (internal name `Dock Bomber`) into something you can summon on demand — as a
normal **enemy** or as a **friendly companion** — and that can also replace the **Cogwork Flier**
tool with a friendly Flintflame Flyer.

- Mod id: `io.github.lifelan.flyersummon`
- Requires: BepInExPack Silksong
- Config file: `BepInEx/config/io.github.lifelan.flyersummon.cfg`

---

## Installation

1. Install **BepInExPack Silksong**.
2. Put `FlyerSummon.dll` in:

   ```
   <game>/BepInEx/plugins/lifelan-FlyerSummon/FlyerSummon.dll
   ```

3. Launch the game. The config file is generated on first run.

---

## Hotkeys

| Action | Default | What it does |
| --- | --- | --- |
| Summon enemy | **F8** | Spawns a normal (hostile) Flintflame Flyer next to Hornet |
| Summon ally | **F9** | Spawns a friendly Flyer that attacks enemies and never harms Hornet |
| Clear allies | **F10** | Removes every friendly Flyer |

Hotkeys can be rebound in the config, or by clicking the button in ConfigurationManager.

---

## The two kinds of Flyer

### Hostile (F8)
- Vanilla behaviour: roams, throws bombs at Hornet and deals damage.
- Good for testing; **never written to the save file**.

### Friendly (F9)
- Auto-targets enemies (anchored near Hornet) and throws bombs from random positions around them.
- Never harms Hornet and is **fully invulnerable** by default (`AllyInvincible`).
- Bombs follow a normal arc: they **pass through Hornet**, explode on enemies on contact, and
  explode after a short delay when they hit the ground or a wall.
- Distance priority: when it drifts too far from Hornet it flies back, and it teleports back after a
  scene transition.
- Count limit: summoning past `MaxCount` still works but **replaces the oldest** Flyer.
- Optional "disappear after N throws".

---

## Cogwork Flier replacement

The mod can replace the **Cogwork Flier** tool entirely:

- throwing the tool spawns a **friendly Flintflame Flyer** instead of a cog;
- the tool **icon** (inventory / HUD / pickup popup) is replaced with a **custom icon bundled in the
  DLL**, rescaled to the original icon's size and left untinted;
- only **Cogwork Flier** is affected — **Cogwork Saw is untouched**;
- about 5 seconds after entering gameplay the Deep Docks assets are preloaded (a brief hitch on the
  first load); afterwards throws are instant;
- if you throw before preloading finishes, the first throw is the vanilla cog and the next one is
  the Flyer.

### Switching back to the vanilla Cogwork Flier
Set:
```
Tool/ReplaceCogworkFlier = false
```
Hot-reloadable (applies within ~1 second, no restart). Existing summoned allies stay until you press
**F10**; the F8/F9 hotkeys are unaffected.

---

## Configuration

> Everything is **hot-reloadable**: ConfigurationManager changes apply instantly, and editing the
> `.cfg` and saving reloads it within ~1 second.

### General

| Key | Default | Meaning |
| --- | --- | --- |
| `Hotkey` | `F8` | Hostile Flyer summon key |
| `FriendlyHotkey` | `F9` | Friendly Flyer summon key |
| `ClearAlliesHotkey` | `F10` | Clear-allies key |
| `SceneAddress` | `Scenes/Dock_02` | Deep Docks scene used as the prefab library |
| `EnemyObjectName` | `Dock Bomber` | Enemy GameObject cloned from the library |
| `DebugLogging` | `false` | Extra logging |

### Summon (spawn position)

| Key | Default | Meaning |
| --- | --- | --- |
| `SpawnOffsetX` | `3` | Horizontal spawn offset from Hornet |
| `SpawnOffsetY` | `1.5` | Vertical spawn offset from Hornet |
| `SpawnRadius` | `3` | Random jitter around the spawn point (avoids stacking); `0` disables |

### Ally (companion behaviour)

| Key | Default | Range | Meaning |
| --- | --- | --- | --- |
| `AllyInvincible` | `true` | bool | Ally is immune to all damage from Hornet |
| `MaxCount` | `3` | 1–20 | Max simultaneous allies; extra summons replace the oldest |
| `EnemySearchRadius` | `25` | 5–80 | Target search radius (anchored at Hornet) |
| `FollowDistance` | `12` | 2–40 | Distance at which the ally returns to Hornet |
| `WarpDistance` | `50` | 15–200 | Hard teleport-back distance (also after scene changes) |
| `OrbitRadius` | `3.5` | 0–12 | Radius of the random spot each ally attacks from; `0` disables |
| `AimSpread` | `1` | 0–10 | Random spread of the bomb landing point; `0` = exact centre |
| `BombDamage` | `15` | 0–1000 | Bomb explosion damage (vanilla is 300) |
| `BombEffectScale` | `1` | 0–1 | Explosion visual size (damage area unchanged; `0` = invisible) |
| `BombVolume` | `0.1` | 0–1 | Bomb volume multiplier (`0` = muted) |
| `ThrowSound` | `false` | bool | Play the ally's throw sound/voice |
| `AwardJournalKill` | `false` | bool | Ally kills count toward the Hunter's Journal |
| `PersistAcrossScenes` | `true` | bool | Ally survives scene transitions |
| `BodyScale` | `1` | 0.2–2 | Ally visual scale (also scales its hitbox and ranges) |
| `AutoDespawnAfterThrows` | `false` | bool | Disappear after a number of throws |
| `AutoDespawnThrowCount` | `5` | 1–100 | Number of throws before disappearing |

### Tool

| Key | Default | Meaning |
| --- | --- | --- |
| `ReplaceCogworkFlier` | `true` | Replace the Cogwork Flier with a friendly Flyer (throw + icon) |

---

## Hardcoded (not configurable)

For a clean view and to avoid polluting vanilla behaviour, the following are fixed:

- **Flame density = 0.2**, **smoke density = 0.01**;
- full-screen flash / vignette: **off**;
- camera shake / gamepad vibration: **off**;
- large particles (`explosion_main_large` / `Flame Over` / `white_balls`): **off**;
- bombs **pass through Hornet** (do not explode on her);
- bombs **never damage Hornet** (all hero-damage on the bomb/explosion is zeroed);
- ally bombs are **destroyed on recycle** so the shared pool is never polluted (otherwise the vanilla
  enemy's bombs would inherit friendly settings).

---

## How it works

The Flintflame Flyer only exists as a **scene object** in Deep Docks; its bomb, sprite atlas and
animations live in **Deep Docks specific Addressables bundles** that unload when you leave. That is
the "Deep Docks restriction".

On first use the mod:

1. loads a small Deep Docks scene via Addressables (keeping the handle so dependencies stay loaded);
2. clones `Dock Bomber` into a hidden, inactive `DontDestroyOnLoad` template;
3. unloads the scene objects so the scene never appears in Unity's scene list;
4. strips `PersistentBoolItem` so summons never read/write the original's save state.

Friendly behaviour is implemented with a few Harmony patches (alert ranges detect enemies instead of
the hero, the FSM's `Hero` variable is retargeted, allies are made invulnerable, the Cogwork Flier's
throw/icon are swapped, throw audio is silenced, …). They only affect the friendly Flyers.

---

## FAQ

**A brief hitch on the first summon?** Normal — the Deep Docks scene is loaded and unloaded once.

**The ally just sits there?** Make sure an enemy is nearby (`EnemySearchRadius`). If it still does
nothing, enable `DebugLogging` and share the log.

**It takes too long to come back?** Lower `FollowDistance` and `WarpDistance`.

**Past the count limit?** The newest still appears and the oldest is removed; press **F10** to clear
everything.

**The Cogwork Flier icon didn't change?** Preloading hasn't finished (within 5 seconds of entering
gameplay) or `ReplaceCogworkFlier` is `false`.

**Does it affect saves?** No. Summons are never written to the save, and leftover allies are cleared
when you return to the main menu.

---

## Building

```sh
dotnet build -c Release
```

The build copies `FlyerSummon.dll` into `BepInEx/plugins/lifelan-FlyerSummon/` and produces a
Thunderstore package in `thunderstore/dist/`. The custom tool icon lives at
`Assets/cogworkflyer_icon.png` and is embedded into the DLL.
