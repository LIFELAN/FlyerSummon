# 燧焰飞工「友军化」可行性报告

> 目标：把 `FlyerSummon` 召唤出的燧焰飞工（内部名 `Dock Bomber`）从普通敌人改成
> **帮助大黄蜂攻击敌人的友军**。
>
> 结论：**可行**。推荐 **方案 A（复用原版 PlayMaker 状态机，只重定向目标）**，
> 相比从零写 AI，代码更少、动作更原汁原味。下面给出逆向依据、实现路径、风险与测试清单。

---

## 1. 逆向分析结论（关键事实）

数据来源：对游戏 `Assembly-CSharp.dll` 反编译 + 对 `dock_02` / `localpoolprefabs_assets_areadocks`
资源包的 UnityPy 解析。

### 1.1 敌人组成

`Dock Bomber`（`layer = 11 = ENEMIES`）挂载：

- 表现：`tk2dSprite` / `tk2dSpriteAnimator` / `AudioSource` / `SpriteFlash` / `Recoil` / `SetZ`
- 受击与死亡：`HealthManager(hp=60)`、`EnemyHitEffectsRegular`、`EnemyDeathEffectsRegular`
- 对英雄伤害：`DamageHero`
- **AI：3 个 `PlayMakerFSM`**
- 资源池：`PersonalObjectPool`（内含 `DF Bomb Rock` 预制体，size=3）
- 存档：`PersistentBoolItem`（本模组已剥离）
- 圣歌/黑丝相关：`NeedolinTextOwner`、`BlackThreadState`、`EnemySingDuration`、`PlayMakerFixedUpdate`

子物体（注意层）：

| 子物体 | 层 | 组件 | 作用 |
| --- | --- | --- | --- |
| `Aggro Range` | 13 HERO_DETECTOR | CircleCollider2D + `AlertRange` | 警戒范围 |
| `Idle Range` | 13 | `AlertRange` | 脱战范围 |
| `Attack Range` | 13 | `AlertRange` | 攻击距离 |
| `Fire Point` / `Fire Point W` | 11 | Transform | 炸弹发射点 |
| `Terrain Box` / `Wall Detector` | 14 | `EnemyWallRange` | 撞墙检测 |
| `Throw Antic Glow` / `Throw Antic Effects` | 11 | — | 投掷前摇特效 |

### 1.2 主状态机 `Behaviour`（61 KB）

- 状态：`Init → Idle Fly → Startle → Attack Fly → Roof Avoid → Attack Antic → Attack Antic 2 → Aim Lock → Throw Projectile → Projectile Min X → Min L/R → Throw Anim → Throw Recover → Attack Fly`
- 关键事件：
  - `CheckAlertRange` 在“进入范围”时发 **`ATTACK`**，在“离开范围”时发 **`CANCEL`**
  - `ATTACK` → `Startle`/`Roof Avoid`；`CANCEL` → 回 `Idle Fly`
  - `PRAY → Sing`、`TOOK DAMAGE → Startle`、`BLOCKED EXPLOSION → Block Explosion`
- **锁定目标**：整个攻击链都读 FSM 的 `FsmGameObject` 变量 **`Hero`**：
  - `Attack Fly`：`DistanceFlyV2.target = Hero`、`FaceObjectV3.ObjectB = Hero`
  - `Aim Lock`：`GetAngleToTarget2D.target = Hero` → 写入 `Angle`
  - `Throw Projectile`：用 `Angle` + `Fire Speed` 给炸弹赋初速，并把生成的炸弹存进 `Spawned Rock`
  - `Init` 里的 `GetHero` 只会把 `Hero` 设成 `HeroController.instance`（**只在 Init 出现一次**）
- `Init` 依据 `Battler / Start Praying / Bomb Ambush` 等 bool 分流；clone 默认全为 false → 走 `Idle Fly`。
- `AlertRange.IsHeroInRange()` 内部其实返回的是“触发范围里有东西”（由层碰撞矩阵决定），
  名字虽叫 Hero，但**判定对象完全由物理层决定**。

### 1.3 敌我判定机制（本作没有 team 字段）

- **大黄蜂的攻击**：`DamageEnemies`（标签 `Nail Attack`），命中 `ENEMIES(11)` 层上的
  `HealthManager`。`DamageEnemies.OnTriggerEnter2D` 会主动忽略
  `HERO_BOX / PLAYER / ENEMY_ATTACK / CORPSE / ATTACK_DETECTOR`。
- **敌人对英雄的伤害**：敌人挂 `DamageHero`（或名为 `damages_hero` 的 FSM），
  由英雄身上的 `HeroBox` 在 `LateFixedUpdate` 里检测重叠后调用 `HeroController.TakeDamage`。
  **没有 `DamageHero` 就不会伤害大黄蜂。**
- **免疫开关**：`HealthManager` 有私有 `invincible`、`immuneToNailAttacks`、`immuneToExplosions` 等；
  `TakeDamage()` 开头 `IsImmuneTo(...)` 命中就直接 return。

### 1.4 炸弹 `DF Bomb Rock`（`layer = 17 = HERO_ATTACK`）

- 自身：`DamageHero.damageDealt = 0`、`DamageEnemies.damageDealt = 0`、`AutoRecycleSelf`、
  `FireballProjectile`（`ExplosionRock = 1`，`IdleLifeTime = 1s`，`ExplodeAnticTime = 0.7s`）。
- 爆炸子物体 `damager`：
  - `DamageHero.damageDealt = 2`（打英雄）
  - `DamageEnemies.damageDealt = 300`、`attackType = 11 (Explosion)`、`AwardJournalKill = 1`
  - `damageFSMEvent = "GAS EXPLOSION"`
- **结论：炸弹本来就有“打敌人”的能力（300 爆炸伤害），只是默认被关掉/只在对英雄时爆炸。**
  只要把爆炸的 `DamageHero` 归零、保留 `DamageEnemies`，就能做到“只打敌人不打大黄蜂”。

---

## 2. 方案 A：复用原 FSM，重定向目标（推荐）

思路：**不重写 AI**，只做 4 件事——把“英雄”替换成“敌人”、让警报范围识别敌人、
把炸弹改成友方、让友军免伤。

### A1. 关闭本体 `DamageHero`
clone 后 `GetComponent<DamageHero>()`：`damageDealt = 0` 且 `enabled = false`。

### A2. 警报范围改为“检测敌人”（Harmony）
用 Harmony **前缀替换** `AlertRange.IsHeroInRange()`：

```csharp
// 只对“属于友军的 AlertRange”生效，其他敌人保持原版行为
[HarmonyPrefix]
static bool AlertRange_IsHeroInRange(AlertRange __instance, ref bool __result)
{
    var ally = __instance.GetComponentInParent<AllyFlyer>();
    if (ally == null) return true;              // 原版逻辑
    __result = ally.HasValidEnemyInRange(__instance);  // 用 Physics2D 查询
    return false;
}
```

`HasValidEnemyInRange` 用 `Physics2D.OverlapCircle` + **显式 `ENEMIES` 层掩码**，
并排除：自己、其他友军（带 `AllyFlyer` 标记）、已死亡/`hp<=0` 的对象。
> 好处：`Physics2D` 查询只吃 `layerMask`、**不受层碰撞矩阵影响**，
> 因此不需要改子物体层、也不受 `lineOfSight` 朝向英雄的限制。

### A3. 每帧把 FSM 变量 `Hero` 指向最近的敌人

```csharp
var fsm = clone.GetComponents<PlayMakerFSM>()
               .First(f => f.FsmName == "Behaviour");
fsm.FsmVariables.GetFsmGameObject("Hero").Value = target;   // target 为最近的敌人
```

- `GetHero` 只在 `Init` 跑一次，之后每帧覆盖即可。
- 没有敌人时把 `Hero` 设回 `HeroController.instance`（或保持原地），友军就进入 `Idle Fly`。

### A4. 友方炸弹

FSM 投弹时会把炸弹存进变量 **`Spawned Rock`**。控制器每帧检查该变量，
发现“新炸弹”后对其配置：

- 遍历炸弹及子物体，把所有 `DamageHero.damageDealt = 0`；
- 爆炸子物体的 `DamageEnemies.damageDealt` 设为可配置值（默认沿用 300，建议做成 `5~15`）；
- `DamageEnemies.AwardJournalKill` 按需设为 `false`（避免友军击杀刷图鉴）。

> 因为 `Spawned Rock` 是官方 FSM 自己写入的引用，拦截**不需要**给
> `SpawnObjectFromGlobalPool` 打补丁，也不会有误伤原版敌人炸弹的问题。

### A5. 友军免伤（可选，建议默认开启）

三选一：

1. 反射设置 `HealthManager.immuneToNailAttacks = true`、`immuneToExplosions = true`
   （只免疫大黄蜂与爆炸，其他仍可受伤）；
2. Harmony 前缀 `HealthManager.Hit`：目标是友军且 `hitInstance.IsHeroDamage` → 返回 `None`（最干净）；
3. 直接 `invincible = true`（会触发弹刀音效，观感一般）。

### A6. 建议顺手处理
- 禁用 `BlackThreadState`（黑丝强化机制可能改变友军数值/攻击）。
- `NeedolinTextOwner` / `EnemySingDuration` 可保留，最多出现“唱歌”小动画，不影响战斗。
- 兼容多个友军：目标筛选、爆炸免伤都按“带 `AllyFlyer` 标记”区分。

---

## 3. 方案 B：完全自定义 AI（备选）

关闭全部 `PlayMakerFSM`，自己写 `MonoBehaviour`：

- 从 `PersonalObjectPool.startupPool[0].prefab` 取 `DF Bomb Rock`；
- 自己实现飞行（Rigidbody2D 加速度 + 限速）、朝向（`tk2dSpriteAnimator` 翻转）、
  投弹时机（播放 `Attack Antic` / `Throw Antic` 等 clip）与瞄准；
- 实例化炸弹后立即配置 `DamageHero = 0` / `DamageEnemies = 可配置`，再赋初速。

优点：完全可控、可预测、不依赖 FSM 内部细节。
缺点：代码量约为方案 A 的 2~3 倍，动画/手感需要反复调，且日后的游戏版本更新要自己维护。

> 结论：除非方案 A 的 FSM 行为实在无法接受，否则不建议走 B。

---

## 4. 风险与未知

| 风险 | 说明 | 缓解 |
| --- | --- | --- |
| FSM 内的英雄专属动作 | `CheckHeroPerformanceRegion`、`EnemySingControl`、`Sing/PRAY` 分支可能偶发触发 | 先实测；必要时屏蔽 `PRAY`/`Sing` 分支或禁用相关组件 |
| 友军互伤 | 爆炸 `DamageEnemies` 会命中 `ENEMIES` 层（含其他友军） | A5 免伤 / 排除带 `AllyFlyer` 的目标 |
| 目标过远 / 无目标 | `DistanceFlyV2` 会追很远 | A3 限制搜索半径，超出则设回英雄或原地待命 |
| 瞬发伤害过高 | 爆炸 300 点，可能秒小怪 | A4 做成可配置伤害 |
| Boss 战 | 部分 Boss 只认英雄、场地机关可能误伤友军 | 实测；属于可接受范围 |
| 版本兼容 | 状态名/变量名来自当前版本 | 用 `FsmVariables.Get...` 时做空值保护，失败则回退为“普通召唤” |
| 存档/图鉴 | 友军击杀会写图鉴击杀数 | 视需求设置 `AwardJournalKill` |
| 层碰撞矩阵 | 原警报范围只探测英雄 | A2 改用 `Physics2D` 查询，绕开矩阵 |

---

## 5. 测试清单

1. 空旷区域召唤 → 友军是否原地/跟随待命，不攻击大黄蜂。
2. 附近刷出敌人 → 是否自动接近并在射程内投弹。
3. 炸弹是否只伤敌人、不掉大黄蜂血；伤害数值符合配置。
4. 连续召唤 2~3 只 → 是否互相攻击、是否被彼此爆炸炸死。
5. 大黄蜂攻击友军 → 是否免伤（或按配置可击杀）。
6. 敌人被清空 → 友军是否回到待命。
7. 过场景 / 大退 → 不写存档、随场景卸载。
8. 深坞原生飞工 → 行为是否仍然正常（Harmony 补丁只作用于友军）。
9. 死亡处理：友军被打死后是否正确消失、无报错。

---

## 6. 建议新增配置

| 配置项 | 默认 | 说明 |
| --- | --- | --- |
| `Ally/Enabled` | `true` | 召唤物是否为友军 |
| `Ally/EnemySearchRadius` | `25` | 索敌半径 |
| `Ally/BombDamage` | `15` | 友方炸弹爆炸伤害 |
| `Ally/AwardJournalKill` | `false` | 友军击杀是否计入图鉴 |
| `Ally/Invincible` | `true` | 友军是否免疫大黄蜂伤害 |
| `Ally/TargetHeroWhenIdle` | `false` | 无敌人时是否跟随大黄蜂 |

---

## 7. 工作量与建议

- **方案 A 预估**：新增约 300~450 行 C#（含 2~3 个 Harmony 补丁、友军标记组件、
  索敌与炸弹配置、配置项），改动集中在 `FlyerSummonController` 的 `Spawn()` 与
  新增 `Ally/` 命名空间下的少量文件。
- **不改变**现有 Addressables 资源保活/模板克隆主体逻辑，风险可控。
- **建议先做最小可用版本（MVP）**：A1 + A2 + A3 + A4，让飞工能“自动打敌人且不误伤大黄蜂”，
  再迭代 A5/A6 与手感调优。

**下一步**：如认可方案 A，我可以直接开始实现 MVP 并编译验证。
