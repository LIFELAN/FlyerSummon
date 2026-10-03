# FlyerSummon 使用说明（燧焰飞工召唤）

一个《空洞骑士：丝之歌》BepInEx 模组：按下热键（默认 **F8**），即可**在任意区域**
于大黄蜂身边召唤一只功能完整的 **燧焰飞工（Flintflame Flyer，游戏内部物体名 `Dock Bomber`）**。

---

## 安装

1. 先安装 **BepInExPack Silksong**。
2. 将 `FlyerSummon.dll` 放入：

   ```
   <游戏目录>/BepInEx/plugins/lifelan-FlyerSummon/FlyerSummon.dll
   ```

   （用模组管理器安装，或自行 `dotnet build -c Release` 构建，构建脚本会自动复制。）
3. 启动游戏。首次运行会在 `BepInEx/config/io.github.lifelan.flyersummon.cfg`
   生成配置文件。

---

## 使用方法

| 操作 | 默认按键 | 效果 |
| --- | --- | --- |
| 召唤敌人 | **F8** | 在大黄蜂身旁召唤一只普通（敌对）燧焰飞工 |
| 召唤友军 | **F9** | 在大黄蜂身旁召唤一只友军燧焰飞工，自动攻击敌人且不伤害大黄蜂 |
| 清除友军 | **F10** | 移除当前所有已召唤的友军飞工 |

- 首次召唤时，模组需要加载深坞（Deep Docks）资源，可能会有一瞬间的卡顿；
  之后每次都直接从缓存好的模板复制，几乎没有延迟。
- **F8** 召唤出的是**正常敌人**，会投掷炸弹、造成伤害，也会攻击玩家，请自行躲闪。
- **F9** 召唤出的是**友军**：自动索敌并投掷炸弹攻击敌人，不会伤害大黄蜂；
  默认免疫大黄蜂的伤害（可用 `Ally/AllyInvincible` 关闭）。
- 友军的炸弹有正常抛物线，**会穿过大黄蜂**（不会因碰到大黄蜂而爆炸），碰到敌人立即爆炸，落到地面/障碍后延迟爆炸。
- 附近没有敌人时，如果离大黄蜂太远（`Ally/FollowDistance`），友军会主动飞回大黄蜂身边；距离判定优先于索敌。
- 默认跨场景保留（`Ally/PersistAcrossScenes`），过场景/拉开过远时会自动传送回大黄蜂身边；回到主菜单一段时间后会自动清除，避免带进别的存档。
- 召唤数量受 `Ally/MaxCount` 限制（默认 3），按 `F10` 可清除全部友军。
- 所有配置均支持热更新：通过 ConfigurationManager 修改立即生效；直接改 `.cfg` 文件也会每约 1 秒自动重载。
- 离场或过场景时会随当前游戏场景正常卸载（敌对飞工），**不会**写入存档。

---

## 配置项

配置文件：`BepInEx/config/io.github.lifelan.flyersummon.cfg`

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `General/Hotkey` | `F8` | 敌对飞工召唤热键，填写 `KeyCode` 名称。装有 ConfigurationManager 时可点击按钮改键。 |
| `General/FriendlyHotkey` | `F9` | 友军飞工召唤热键。 |
| `General/ClearAlliesHotkey` | `F10` | 清除所有友军飞工的热键。 |
| `General/SceneAddress` | `Scenes/Dock_02` | 用作“预制体仓库”的深坞场景 Addressables 地址。若加载失败，可尝试 `scenes/dock_02`。 |
| `General/EnemyObjectName` | `Dock Bomber` | 从仓库场景中克隆的敌人物体名。 |
| `Summon/SpawnOffsetX` | `3` | 相对大黄蜂的水平生成偏移。 |
| `Summon/SpawnOffsetY` | `1.5` | 相对大黄蜂的垂直生成偏移。 |
| `Summon/SpawnRadius` | `3` | 在生成点周围随机散布的半径，避免多只飞工叠在一起；`0` 关闭。 |
| `Ally/EnemySearchRadius` | `25` | 友军索敌半径。 |
| `Ally/FollowDistance` | `12` | 友军远离大黄蜂超过该值就优先回飞。 |
| `Ally/WarpDistance` | `50` | 硬性传送兜底距离（过远/过场景时直接瞬移回大黄蜂）。 |
| `Ally/AimSpread` | `1` | 炸弹落点在敌人周围的随机散布半径；`0` 为正中瞄准。 |
| `Ally/OrbitRadius` | `3.5` | 每只飞工在敌人周围**随机方位**占位、从不同方向投弹；`0` 关闭。 |
| `Ally/MaxCount` | `3` | 同时存在的友军上限；超出时召唤仍生效，但会销毁最早生成的那只。 |
| `Ally/AutoDespawnAfterThrows` | `false` | 是否开启“投弹 N 次后自动消失”。关闭时飞工一直存在，只能用 F10 清除。 |
| `Ally/AutoDespawnThrowCount` | `5` | 开启自动消失时，投弹多少次后消失。 |
| `Tool/ReplaceCogworkFlier` | `true` | 是否把「齿轮蜂」工具替换为友军燧焰飞工（投出的飞工 + 图标）。 |
| `Ally/BombDamage` | `15` | 友军炸弹爆炸对敌人的伤害（原版为 300）。 |
| `Ally/BombVolume` | `0.1` | 友军炸弹音量倍率（飞行音 + 爆炸音），`0` 为静音；只影响友军。 |
| `Ally/BombEffectScale` | `1` | 友军爆炸**视觉大小**（`0~1`，伤害范围保持不变，`0` 为不可见）。 |

> 友军炸弹的**火焰浓度（0.2）、烟雾浓度（0.01）**以及全屏闪光/暗角、相机震动/振动、大粒子等已**写死**，不提供配置。
| `Ally/ThrowSound` | `false` | 友军投弹是否播放原版投掷音效/语音（默认静音）。 |
| `Ally/BodyScale` | `1` | 友军**本体视觉大小**（`0.2~2`，同时缩放碰撞箱与警戒范围）。 |

> 友军炸弹的**全屏闪光/暗角、相机震动/振动、大粒子**一律关闭（已写死，不提供配置），保证视野清晰。
| `Ally/AwardJournalKill` | `false` | 友军击杀是否计入猎人日志。 |
| `Ally/AllyInvincible` | `true` | 友军是否免疫大黄蜂造成的一切伤害（含钉子、工具、技能、爆炸）。 |
| `Ally/PersistAcrossScenes` | `true` | 友军是否跨场景保留，并在新场景自动回到大黄蜂身边。 |
| `General/DebugLogging` | `false` | 输出额外调试日志到 BepInEx 控制台/日志。 |

---

## 为什么不能简单地“直接生成预制体”

燧焰飞工在深坞之外很难召唤，原因如下：

- 它只作为深坞场景（`dock_02` / `dock_03c`）里的**场景物体**存在，
  并没有独立的 Addressables 预制体。
- 它的炸弹（`DF Bomb Rock`）、精灵图集（`tk2dcollections_assets_areadocks`）和动画
  （`tk2danimations_assets_areadocks`）都在**深坞专用的 Addressables 资源包**里。
- 离开深坞后这些资源包会被卸载，敌人虽然还会播放投掷动画，
  但 `PersonalObjectPool` 会丢弃空的炸弹条目，`SpawnObjectFromGlobalPool`
  悄悄生成失败——于是完全打不出伤害。这就是所谓的“深坞限制”。

## 工作原理

首次召唤时，模组会：

1. 通过 Addressables 加载一个小型深坞场景，
   使用 `SceneReleaseMode.OnlyReleaseSceneOnHandleRelease` 且 `activateOnLoad: true`。
2. 把 `Dock Bomber` 克隆到一个隐藏、未激活的 `DontDestroyOnLoad` 模板里。
3. 用 `SceneManager.UnloadSceneAsync` **卸载场景内的物体**，使该场景不出现在
   Unity 的场景列表中，不会干扰游戏自身的场景切换。
4. **保留 Addressables 句柄**，从而让所有依赖资源包（炸弹、图集、动画……）
   在整个游戏会话中保持加载。

之后每次按热键，只是把模板复制到大黄蜂所在位置。召唤出的敌人会被移动到当前
游戏场景中，因此会随场景正常卸载；同时其 `PersistentBoolItem` 会被移除，
所以它们永远不会读取或写回原版敌人的存档状态。

---

## 齿轮蜂替换

模组可以把工具 **「齿轮蜂（Cogwork Flier）」** 整套替换成友军燧焰飞工：

- 使用齿轮蜂时，投出的不再是齿轮蜂，而是会自动索敌、投弹攻击敌人的**友军燧焰飞工**；
- 工具**图标**（背包 / HUD / 弹窗）替换为燧焰飞工的图鉴图标，并按原齿轮蜂图标的尺寸重新缩放以适配 HUD；
- 投掷同样受 `Ally/MaxCount` 上限限制：超出时仍会抛出，但会顶掉最早的那只；
- 只替换 **Cogwork Flier（齿轮蜂）**，不会影响 **Cogwork Saw（机轮刃）**；
- 进入游戏后约 5 秒会预加载一次深坞资源（首次可能轻微卡顿），之后即用即投；
- 如果预加载尚未完成就投掷，第一次会先出原版齿轮蜂，下次即为飞工；
- 可用 `Tool/ReplaceCogworkFlier = false` 关闭整个替换。

## 注意事项

- 首次召唤会临时加载并卸载一个深坞场景，属于正常现象；日志中会显示相关记录。
- 如果日志提示 `was not found in library scene`，请检查 `SceneAddress` 与
  `EnemyObjectName` 是否与你的游戏版本一致。
- 模组只读取并克隆资源，不改动存档数据。

---

## 构建

```sh
dotnet build -c Release
```

构建完成后会：

- 将 `FlyerSummon.dll` 复制到 `BepInEx/plugins/lifelan-FlyerSummon/`；
- 在 `thunderstore/dist/` 生成 Thunderstore 安装包。

游戏路径在 `SilksongPath.props` 中配置。
