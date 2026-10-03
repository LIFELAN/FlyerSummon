using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GlobalEnums;
using HutongGames.PlayMaker;
using UnityEngine;

namespace FlyerSummon;

/// <summary>
/// Marks a summoned Flintflame Flyer as a friendly companion and drives the logic the vanilla
/// PlayMaker FSM needs in order to fight enemies instead of Hornet:
/// <list type="bullet">
/// <item>retargets the FSM's <c>Hero</c> variable at the nearest enemy, or back at Hornet when too far;</item>
/// <item>answers the FSM's range checks with enemies instead of Hornet;</item>
/// <item>flies back to Hornet directly when it drifts too far (the vanilla AI wanders);</item>
/// <item>turns the thrown bomb into a friendly one (no hero damage, configurable enemy damage).</item>
/// </list>
/// Runs late so its movement override wins over the FSM's own velocity writes.
/// </summary>
[DefaultExecutionOrder(20000)]
internal sealed class AllyFlyer : MonoBehaviour
{
    private const string BehaviourFsmName = "Behaviour";

    private static readonly int EnemyLayerMask = 1 << (int)PhysLayers.ENEMIES;

    private static readonly List<AllyFlyer> ActiveFlyers = new List<AllyFlyer>();

    private PlayMakerFSM? _behaviourFsm;
    private FsmGameObject? _heroVar;
    private Rigidbody2D? _body;

    private AlertRange[] _ranges = System.Array.Empty<AlertRange>();
    private string? _lastLoggedState;
    private bool _sentBattleStart;
    private bool _followHero;
    private int _throwCount;
    private float _despawnTimer = -1f;
    private GameObject? _aimProxy;
    private Vector2 _aimOffset;
    private GameObject? _currentEnemy;
    private Vector2 _orbitOffset;
    private bool _initialized;
    private Vector3 _baseScale = Vector3.one;
    private float _appliedBodyScale = float.NaN;

    internal static bool AnyActive => ActiveFlyers.Count > 0;

    internal static int ActiveCount => ActiveFlyers.Count;

    /// <summary>Called on the inactive clone before it is activated, or automatically from Awake.</summary>
    internal void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        _baseScale = transform.localScale;

        // The body itself must never damage Hornet.
        foreach (var damager in GetComponentsInChildren<DamageHero>(true))
        {
            damager.damageDealt = 0;
        }

        // Black thread is a hostile buff mechanic; keep it off the companion.
        var blackThread = GetComponent<BlackThreadState>();
        if (blackThread != null)
        {
            blackThread.enabled = false;
        }

        if (FlyerSummonPlugin.PersistAcrossScenes.Value && transform.parent == null)
        {
            DontDestroyOnLoad(gameObject);
        }

        FlyerSummonPlugin.LogInfo($"Ally flyer initialized on '{name}'.");
    }

    private void Awake()
    {
        // Tool-spawned flyers are created active, so initialise here as well.
        Initialize();
    }

    private void OnEnable()
    {
        if (!ActiveFlyers.Contains(this))
        {
            ActiveFlyers.Add(this);
        }

        EnforceLimit();
    }

    /// <summary>
    /// Keeps at most <c>Ally/MaxCount</c> flyers alive by replacing the oldest ones.
    /// </summary>
    internal static void EnforceLimit()
    {
        var max = Mathf.Max(1, FlyerSummonPlugin.MaxCount.Value);
        while (ActiveFlyers.Count > max)
        {
            var oldest = ActiveFlyers[0];
            ActiveFlyers.RemoveAt(0);
            if (oldest == null)
            {
                continue;
            }

            FlyerSummonPlugin.LogInfo(
                $"Friendly flyer limit {max} reached; replacing oldest '{oldest.name}'.");
            Destroy(oldest.gameObject);
        }
    }

    private void OnDisable()
    {
        ActiveFlyers.Remove(this);

        if (_aimProxy != null)
        {
            Destroy(_aimProxy);
            _aimProxy = null;
        }
    }

    /// <summary>Destroys every live friendly flyer but keeps the mod's summoning available.</summary>
    internal static void RemoveAllInstances()
    {
        for (var i = ActiveFlyers.Count - 1; i >= 0; i--)
        {
            var flyer = ActiveFlyers[i];
            if (flyer != null)
            {
                Destroy(flyer.gameObject);
            }
        }

        ActiveFlyers.Clear();
    }

    /// <summary>Destroys every live friendly flyer (used by the clear hotkey).</summary>
    internal static void Clear()
    {
        RemoveAllInstances();
        FlyerSummonPlugin.LogInfo("Removed all friendly flyers.");
    }

    private void Start()
    {
        _body = GetComponent<Rigidbody2D>();
        _behaviourFsm = FindBehaviourFsm();
        _ranges = GetComponentsInChildren<AlertRange>(true);

        if (_behaviourFsm == null)
        {
            FlyerSummonPlugin.LogError($"Ally flyer '{name}' has no '{BehaviourFsmName}' FSM; it will stay passive.");
            return;
        }

        _heroVar = _behaviourFsm.FsmVariables.GetFsmGameObject("Hero");

        var rangeNames = new StringBuilder();
        foreach (var range in _ranges)
        {
            rangeNames.Append(range.name).Append(' ');
        }

        FlyerSummonPlugin.LogInfo(
            $"Ally flyer '{name}' ready. FSM='{_behaviourFsm.FsmName}', ranges=[{rangeNames}], "
            + $"HeroVar={(_heroVar != null ? "ok" : "MISSING")}.");
    }

    private void Update()
    {
        ApplyBodyScale();

        if (_behaviourFsm == null)
        {
            return;
        }

        var hero = HeroController.instance;
        var heroDistance = hero != null
            ? Vector2.Distance(transform.position, hero.transform.position)
            : 0f;

        // Hard fallback: after a scene transition (or if the ally falls far behind) snap it back.
        if (hero != null && hero.isHeroInPosition && heroDistance > FlyerSummonPlugin.WarpDistance.Value)
        {
            transform.position = FlyerSummonPlugin.GetSpawnPosition(hero.transform.position);
            FlyerSummonPlugin.LogInfo($"Ally '{name}' warped back to Hornet (was {heroDistance:F0} away).");
            heroDistance = Vector2.Distance(transform.position, hero.transform.position);
        }

        // Distance takes priority: too far from Hornet means "come back", regardless of enemies.
        // Two thresholds (enter/exit) avoid flip-flopping around the boundary.
        var followDistance = FlyerSummonPlugin.FollowDistance.Value;
        if (hero == null)
        {
            _followHero = false;
        }
        else
        {
            _followHero = _followHero
                ? heroDistance > followDistance
                : heroDistance > followDistance + 2f;
        }

        GameObject? enemy = null;
        if (!_followHero)
        {
            var origin = hero != null ? (Vector2)hero.transform.position : (Vector2)transform.position;
            enemy = FindNearestEnemy(origin, FlyerSummonPlugin.EnemySearchRadius.Value, gameObject);
        }

        if (enemy != _currentEnemy)
        {
            _currentEnemy = enemy;
            RollOrbit();
        }

        if (_heroVar != null)
        {
            if (enemy != null)
            {
                // Aim at a random point near the enemy so several flyers don't stack their shots.
                EnsureAimProxy();
                _aimProxy!.transform.position = (Vector2)enemy.transform.position + _aimOffset;
                _heroVar.Value = _aimProxy;
            }
            else
            {
                _heroVar.Value = hero?.gameObject;
            }
        }

        Diagnose(enemy, heroDistance);
        UpdateAutoDespawn();
    }

    private void UpdateAutoDespawn()
    {
        if (_despawnTimer < 0f)
        {
            return;
        }

        _despawnTimer -= Time.deltaTime;
        if (_despawnTimer > 0f)
        {
            return;
        }

        FlyerSummonPlugin.LogInfo($"Ally '{name}' auto-despawned after {_throwCount} throw(s).");
        Destroy(gameObject);
    }

    private void ApplyBodyScale()
    {
        var bodyScale = FlyerSummonPlugin.BodyScale.Value;
        if (Mathf.Approximately(bodyScale, _appliedBodyScale))
        {
            return;
        }

        _appliedBodyScale = bodyScale;

        // Preserve the current facing sign set by the vanilla FaceObject action.
        var current = transform.localScale;
        transform.localScale = new Vector3(
            (current.x < 0f ? -1f : 1f) * Mathf.Abs(_baseScale.x) * bodyScale,
            (current.y < 0f ? -1f : 1f) * Mathf.Abs(_baseScale.y) * bodyScale,
            _baseScale.z);
    }

    /// <summary>
    /// The vanilla flying AI wanders. While returning to Hornet (or holding an attack position) we
    /// drive the rigidbody directly, running late so our velocity wins over the FSM's own writes.
    /// </summary>
    private void FixedUpdate()
    {
        if (_body == null)
        {
            return;
        }

        // 1) Returning to Hornet takes priority.
        if (_followHero)
        {
            var hero = HeroController.instance;
            if (hero == null)
            {
                return;
            }

            var heroPos = (Vector2)hero.transform.position;
            var pos = (Vector2)transform.position;
            var toHero = heroPos - pos;
            var heroDistance = toHero.magnitude;
            if (heroDistance < 0.2f)
            {
                _body.linearVelocity = Vector2.zero;
                return;
            }

            var heroSpeed = Mathf.Clamp(heroDistance * 2.5f, 5f, 18f);
            _body.linearVelocity = toHero / heroDistance * heroSpeed;
            return;
        }

        // 2) Attacking: each flyer holds a random spot around the target, but let the FSM steer
        // during the aim/throw animation so the bomb still aims properly.
        if (_currentEnemy == null || IsThrowSequence(_behaviourFsm?.Fsm.ActiveStateName))
        {
            return;
        }

        var target = (Vector2)_currentEnemy.transform.position + _orbitOffset;
        var selfPos = (Vector2)transform.position;
        var toTarget = target - selfPos;
        var distance = toTarget.magnitude;
        if (distance > 0.3f)
        {
            var speed = Mathf.Clamp(distance * 2.5f, 3f, 14f);
            _body.linearVelocity = toTarget / distance * speed;
        }
        else
        {
            _body.linearVelocity *= 0.85f;
        }
    }

    private static bool IsThrowSequence(string? state)
    {
        switch (state)
        {
            case "Attack Antic":
            case "Attack Antic 2":
            case "Aim Lock":
            case "Throw Projectile":
            case "Projectile Min X":
            case "Min L":
            case "Min R":
            case "Throw Anim":
            case "Throw Recover":
            case "Block Explosion":
            case "Block End":
                return true;
            default:
                return false;
        }
    }

    private void RollOrbit()
    {
        var radius = FlyerSummonPlugin.OrbitRadius.Value;
        if (radius <= 0f)
        {
            _orbitOffset = Vector2.zero;
            return;
        }

        var angle = UnityEngine.Random.value * Mathf.PI * 2f;
        var distance = radius * UnityEngine.Random.Range(0.65f, 1f);
        _orbitOffset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
    }

    private void Diagnose(GameObject? enemy, float heroDistance)
    {
        var state = _behaviourFsm!.Fsm.ActiveStateName;

        if (state != _lastLoggedState)
        {
            _lastLoggedState = state;
            FlyerSummonPlugin.LogInfo($"Ally '{name}' state -> {state}");

            if (state == "Attack Antic")
            {
                // Roll a fresh, random aim point for this throw.
                var spread = FlyerSummonPlugin.AimSpread.Value;
                _aimOffset = spread > 0f ? UnityEngine.Random.insideUnitCircle * spread : Vector2.zero;
            }
        }

        // Dormant enemies wait for the battle system; a summoned companion never gets that event.
        if (!_sentBattleStart && state == "Dormant")
        {
            _sentBattleStart = true;
            _behaviourFsm.Fsm.Event("BATTLE START");
            FlyerSummonPlugin.LogInfo($"Ally '{name}' was dormant; sent BATTLE START.");
        }

        if (Time.frameCount % 180 != 0)
        {
            return;
        }

        var report = new StringBuilder();
        foreach (var range in _ranges)
        {
            report.Append(range.name).Append('=').Append(HasEnemyInRange(range) ? 'Y' : 'n').Append(' ');
        }

        FlyerSummonPlugin.LogInfo(
            $"Ally '{name}' diag: state={state}, enemy={(enemy != null ? enemy.name : "none")}, "
            + $"heroDist={heroDistance:F1}, follow={_followHero}, ranges: {report}");
    }

    private void EnsureAimProxy()
    {
        if (_aimProxy == null)
        {
            _aimProxy = new GameObject("FlyerSummon Aim Proxy");
        }
    }

    private PlayMakerFSM? FindBehaviourFsm()
    {
        foreach (var fsm in GetComponents<PlayMakerFSM>())
        {
            if (fsm.FsmName == BehaviourFsmName)
            {
                return fsm;
            }
        }

        return null;
    }

    /// <summary>Called by the spawn patch when this ally throws a bomb.</summary>
    internal void OnAllyBombSpawned(GameObject bomb)
    {
        _throwCount++;
        FlyerSummonPlugin.LogInfo($"Ally '{name}' threw bomb #{_throwCount}.");

        // Pick a fresh attack position for the next volley.
        RollOrbit();

        if (FlyerSummonPlugin.AutoDespawnAfterThrows.Value
            && _despawnTimer < 0f
            && _throwCount >= FlyerSummonPlugin.AutoDespawnThrowCount.Value)
        {
            // Give the last bomb a moment to land and explode before vanishing.
            _despawnTimer = 1.5f;
        }

        if (bomb.GetComponent<AllyBomb>() != null)
        {
            return;
        }

        bomb.AddComponent<AllyBomb>();
        ConfigureBomb(bomb);
    }

    private void ConfigureBomb(GameObject bomb)
    {
        var projectile = bomb.GetComponent<FireballProjectile>();
        if (projectile != null)
        {
            // The vanilla rock explodes on contact with the player; a friendly bomb must pass through
            // Hornet and let AllyBomb decide when to explode (enemies, terrain, lava).
            projectile.ExplosionRock = false;
        }

        // The bomb and its explosion must never hurt Hornet.
        foreach (var damager in bomb.GetComponentsInChildren<DamageHero>(true))
        {
            damager.damageDealt = 0;
        }

        // Only the explosion deals ally damage; the flying rock itself stays harmless.
        foreach (var damager in bomb.GetComponentsInChildren<DamageEnemies>(true))
        {
            var isExplosion = projectile != null && projectile.ExplosionChild != null
                              && (damager.transform == projectile.ExplosionChild.transform
                                  || damager.transform.IsChildOf(projectile.ExplosionChild.transform));
            damager.damageDealt = isExplosion ? FlyerSummonPlugin.BombDamage.Value : 0;
            damager.AwardJournalKill = FlyerSummonPlugin.AwardJournalKill.Value;
        }

        FlyerSummonPlugin.LogInfo(
            $"Configured friendly bomb '{bomb.name}' (damage {FlyerSummonPlugin.BombDamage.Value}, "
            + $"effectScale {FlyerSummonPlugin.BombEffectScale.Value}, volume {FlyerSummonPlugin.BombVolume.Value}).");
    }

    /// <summary>
    /// Used by <see cref="AllyPatches"/> to answer the vanilla FSM's "is the target in range?" checks
    /// with enemies instead of Hornet.
    /// </summary>
    internal bool HasEnemyInRange(AlertRange range)
    {
        var isEngagementRange = range.name.IndexOf("Aggro", System.StringComparison.OrdinalIgnoreCase) >= 0
                                || range.name.IndexOf("Idle", System.StringComparison.OrdinalIgnoreCase) >= 0;

        if (_followHero)
        {
            // Returning to Hornet: keep the FSM flying, but never consider the attack range active.
            return isEngagementRange;
        }

        // Some ranges (e.g. Attack Range) carry several colliders and the first one can be an empty
        // polygon. Use the combined bounds of every collider instead of just the first one.
        var colliders = range.GetComponents<Collider2D>();
        Bounds bounds;
        if (colliders.Length == 0)
        {
            bounds = new Bounds(range.transform.position, Vector3.one);
        }
        else
        {
            bounds = colliders[0].bounds;
            for (var i = 1; i < colliders.Length; i++)
            {
                bounds.Encapsulate(colliders[i].bounds);
            }
        }

        var hits = Physics2D.OverlapBoxAll(bounds.center, bounds.size, 0f, EnemyLayerMask);
        foreach (var hit in hits)
        {
            if (IsValidEnemy(hit, gameObject))
            {
                return true;
            }
        }

        if (!isEngagementRange)
        {
            return false;
        }

        // Aggro/Idle decide whether the ally engages at all. The vanilla triggers are sized for
        // Hornet, so widen them to the configured search radius to make the companion proactive.
        var radius = Mathf.Max(
            Mathf.Max(bounds.extents.x, bounds.extents.y),
            FlyerSummonPlugin.EnemySearchRadius.Value);
        var circleHits = Physics2D.OverlapCircleAll(bounds.center, radius, EnemyLayerMask);
        foreach (var hit in circleHits)
        {
            if (IsValidEnemy(hit, gameObject))
            {
                return true;
            }
        }

        return false;
    }

    internal static GameObject? FindNearestEnemy(Vector2 origin, float radius, GameObject? self)
    {
        var hits = Physics2D.OverlapCircleAll(origin, radius, EnemyLayerMask);

        GameObject? best = null;
        var bestSqr = float.MaxValue;
        foreach (var hit in hits)
        {
            if (!IsValidEnemy(hit, self))
            {
                continue;
            }

            var health = hit.GetComponentInParent<HealthManager>();
            var sqr = ((Vector2)health.transform.position - origin).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = health.gameObject;
            }
        }

        return best;
    }

    private static bool IsValidEnemy(Collider2D? collider, GameObject? self = null)
    {
        if (collider == null)
        {
            return false;
        }

        var health = collider.GetComponentInParent<HealthManager>();
        if (health == null || health.isDead)
        {
            return false;
        }

        var enemy = health.gameObject;
        if (self != null && (enemy == self || enemy.transform.IsChildOf(self.transform)))
        {
            return false;
        }

        // Never treat other friendly flyers as enemies.
        return enemy.GetComponentInParent<AllyFlyer>() == null;
    }
}

/// <summary>
/// Gives a friendly bomb a normal arc and makes it explode on enemies and on terrain. The vanilla
/// rock is a trigger that ignores the level and only explodes on Hornet, so we add the missing
/// checks here without touching the shared prefab.
/// </summary>
internal sealed class AllyBomb : MonoBehaviour
{
    private const float HitRadius = 0.6f;

    private static readonly int EnemyLayerMask = 1 << (int)PhysLayers.ENEMIES;
    private static readonly int TerrainLayerMask = 1 << (int)PhysLayers.TERRAIN;

    private Rigidbody2D? _body;
    private FireballProjectile? _projectile;
    private Vector2 _lastPosition;
    private bool _landed;

    // Appearance cache: captured once per pooled instance, re-applied on every throw so both pooled
    // reuse and hot-reloaded config values work.
    private bool _appearanceCaptured;
    private GameObject? _explosion;
    private GameObject? _orangeFlash;
    private readonly List<ParticleSystem> _particles = new();
    private readonly List<float> _particleSizes = new();
    private readonly List<float> _particleRates = new();
    private readonly List<bool> _particleUnderExplosion = new();
    private readonly List<bool> _particleIsSmoke = new();
    private readonly List<ParticleSystem.MinMaxGradient> _particleColors = new();
    private readonly List<ParticleSystem.Burst[]> _particleBursts = new();
    private readonly List<SpriteRenderer> _impactRenderers = new();
    private readonly List<Color> _impactColors = new();
    private readonly List<CameraControlAnimationEvents> _cameras = new();
    private Vector3 _explosionScale;
    private Transform? _damagerTransform;
    private Vector3 _damagerScale;
    private readonly List<GameObject> _bigEffects = new();
    private readonly List<AudioSource> _sources = new();
    private readonly List<float> _sourceVolumes = new();
    private readonly List<FieldInfo> _soundFields = new();
    private readonly List<float> _soundVolumes = new();
    private float _appliedScale = float.NaN;
    private float _appliedVolume = float.NaN;
    private const float FlameDensity = 0.2f;
    private const float SmokeDensity = 0.01f;

    private static readonly string[] BigEffectNames = { "explosion_main_large", "Flame Over", "white_balls" };
    private static readonly string[] SoundFieldNames = { "deathSound", "knockbackSound", "bounceSound" };

    private void Awake()
    {
        _body = GetComponent<Rigidbody2D>();
        _projectile = GetComponent<FireballProjectile>();
        _lastPosition = transform.position;
    }

    private void OnEnable()
    {
        _appliedScale = float.NaN;
        _appliedVolume = float.NaN;
        ApplyAppearance();
    }

    private void OnDisable()
    {
        // Never return this modified instance to the shared bomb pool: the vanilla enemy uses the
        // same pool, so a recycled instance would make its bombs friendly too.
        Destroy(gameObject);
    }

    private void FixedUpdate()
    {
        if (_projectile == null)
        {
            return;
        }

        // Make sure the explosion uses the configured damage even if the pooled instance was reset.
        ApplyExplosionDamage();
        ApplyAppearance();

        var position = (Vector2)transform.position;

        // Explode as soon as the rock touches an enemy (or crosses one between frames).
        var travel = position - _lastPosition;
        if (travel.sqrMagnitude > 0.0001f)
        {
            var castHits = Physics2D.CircleCastAll(
                _lastPosition, HitRadius, travel.normalized, travel.magnitude, EnemyLayerMask);
            foreach (var hit in castHits)
            {
                if (IsHostileEnemy(hit.collider))
                {
                    _projectile.Break();
                    return;
                }
            }
        }
        else
        {
            var overlapHits = Physics2D.OverlapCircleAll(position, HitRadius, EnemyLayerMask);
            foreach (var hit in overlapHits)
            {
                if (IsHostileEnemy(hit))
                {
                    _projectile.Break();
                    return;
                }
            }
        }

        _lastPosition = position;

        if (_landed)
        {
            return;
        }

        // Land on terrain (floor or wall): stop the rock and let the vanilla explosion timer run,
        // which gives the delayed ground explosion.
        if (Physics2D.OverlapCircle(position, HitRadius, TerrainLayerMask) != null)
        {
            _landed = true;
            if (_body != null)
            {
                _body.linearVelocity = Vector2.zero;
                _body.angularVelocity = 0f;
                _body.bodyType = RigidbodyType2D.Kinematic;
            }
        }
    }

    private void CaptureAppearance()
    {
        if (_appearanceCaptured)
        {
            return;
        }

        _explosion = _projectile != null ? _projectile.ExplosionChild : null;
        if (_explosion == null)
        {
            return;
        }

        _appearanceCaptured = true;

        foreach (var particle in GetComponentsInChildren<ParticleSystem>(true))
        {
            _particles.Add(particle);
            _particleSizes.Add(particle.main.startSizeMultiplier);
            _particleRates.Add(particle.emission.rateOverTimeMultiplier);
            _particleUnderExplosion.Add(particle.transform.IsChildOf(_explosion.transform));
            _particleIsSmoke.Add(
                particle.gameObject.name.IndexOf("smoke", System.StringComparison.OrdinalIgnoreCase) >= 0);
            _particleColors.Add(particle.main.startColor);

            var bursts = new ParticleSystem.Burst[particle.emission.burstCount];
            if (bursts.Length > 0)
            {
                particle.emission.GetBursts(bursts);
            }

            _particleBursts.Add(bursts);
        }

        foreach (var renderer in _explosion.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (renderer.gameObject.name.IndexOf("explosion_impact_effect", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _impactRenderers.Add(renderer);
                _impactColors.Add(renderer.color);
            }
        }

        _orangeFlash = FindChild(_explosion.transform, "orange flash")?.gameObject;
        _explosionScale = _explosion.transform.localScale;

        var damager = _explosion.GetComponentInChildren<DamageEnemies>(true);
        if (damager != null)
        {
            _damagerTransform = damager.transform;
            _damagerScale = damager.transform.localScale;
        }

        foreach (var camera in _explosion.GetComponentsInChildren<CameraControlAnimationEvents>(true))
        {
            _cameras.Add(camera);
        }

        foreach (var name in BigEffectNames)
        {
            var transform = FindChild(_explosion.transform, name);
            if (transform != null)
            {
                _bigEffects.Add(transform.gameObject);
            }
        }

        // Screen clutter is always disabled for friendly bombs: camera shake/vibration, the
        // full-screen flash/vignette and the oversized particles.
        foreach (var camera in _cameras)
        {
            if (camera != null)
            {
                camera.enabled = false;
            }
        }

        if (_orangeFlash != null)
        {
            _orangeFlash.SetActive(false);
        }

        foreach (var effect in _bigEffects)
        {
            if (effect != null)
            {
                effect.SetActive(false);
            }
        }

        foreach (var source in GetComponentsInChildren<AudioSource>(true))
        {
            _sources.Add(source);
            _sourceVolumes.Add(source.volume);
        }

        if (_projectile == null)
        {
            return;
        }

        foreach (var fieldName in SoundFieldNames)
        {
            var field = typeof(FireballProjectile).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                _soundFields.Add(field);
                _soundVolumes.Add(((AudioEvent)field.GetValue(_projectile)).Volume);
            }
        }
    }

    private void ApplyAppearance()
    {
        CaptureAppearance();
        if (!_appearanceCaptured || _explosion == null)
        {
            return;
        }

        var scale = FlyerSummonPlugin.BombEffectScale.Value;
        var volume = FlyerSummonPlugin.BombVolume.Value;
        var smoke = SmokeDensity;
        var flame = FlameDensity;
        if (Mathf.Approximately(scale, _appliedScale) && Mathf.Approximately(volume, _appliedVolume))
        {
            return;
        }

        _appliedScale = scale;
        _appliedVolume = volume;

        // Scale the whole explosion via its parent transform: this also scales the Animator-driven
        // sprite fireball (which ignores particle scaling). Particle start sizes are scaled directly.
        // The damage collider is counter-scaled so the damage area stays the same.
        _explosion.transform.localScale = _explosionScale * scale;

        if (_damagerTransform != null && scale > 0.001f)
        {
            _damagerTransform.localScale = _damagerScale / scale;
        }

        // Smoke thickness and flame opacity/density for friendly explosions.
        for (var i = 0; i < _particles.Count; i++)
        {
            if (_particles[i] == null)
            {
                continue;
            }

            var isSmoke = _particleIsSmoke[i];
            var underExplosion = _particleUnderExplosion[i];
            var density = isSmoke ? smoke : (underExplosion ? flame : 1f);
            var size = _particleSizes[i] * (isSmoke ? smoke : 1f);

            // Fluffier flame: as flame goes down, make the particles bigger while fainter/sparser,
            // so it reads as a puffy cloud instead of a dense ball.
            if (underExplosion && !isSmoke)
            {
                size *= Mathf.Lerp(1f, 1.8f, 1f - flame);
            }

            var main = _particles[i].main;
            if (underExplosion && main.scalingMode != ParticleSystemScalingMode.Hierarchy)
            {
                size *= scale;
            }

            main.startSizeMultiplier = size;

            if (underExplosion && !isSmoke)
            {
                main.startColor = ScaleColor(_particleColors[i], flame);
            }

            var emission = _particles[i].emission;
            emission.rateOverTimeMultiplier = _particleRates[i] * density;

            // Explosion particles usually spawn from bursts, so scale those counts too.
            var bursts = _particleBursts[i];
            if (bursts.Length > 0)
            {
                var scaled = new ParticleSystem.Burst[bursts.Length];
                for (var j = 0; j < bursts.Length; j++)
                {
                    scaled[j] = bursts[j];
                    scaled[j].minCount = (short)Mathf.Max(0, Mathf.RoundToInt(bursts[j].minCount * density));
                    scaled[j].maxCount = (short)Mathf.Max(0, Mathf.RoundToInt(bursts[j].maxCount * density));
                }

                emission.SetBursts(scaled);
            }
        }

        for (var i = 0; i < _impactRenderers.Count; i++)
        {
            if (_impactRenderers[i] == null)
            {
                continue;
            }

            var color = _impactColors[i];
            color.a *= flame;
            _impactRenderers[i].color = color;
        }

        for (var i = 0; i < _sources.Count; i++)
        {
            if (_sources[i] != null)
            {
                _sources[i].volume = _sourceVolumes[i] * volume;
            }
        }

        if (_projectile == null)
        {
            return;
        }

        for (var i = 0; i < _soundFields.Count; i++)
        {
            var audioEvent = (AudioEvent)_soundFields[i].GetValue(_projectile);
            audioEvent.Volume = _soundVolumes[i] * volume;
            _soundFields[i].SetValue(_projectile, audioEvent);
        }
    }

    private static ParticleSystem.MinMaxGradient ScaleColor(ParticleSystem.MinMaxGradient gradient, float factor)
    {
        switch (gradient.mode)
        {
            case ParticleSystemGradientMode.Color:
            {
                var color = gradient.color;
                color.a *= factor;
                gradient.color = color;
                break;
            }

            case ParticleSystemGradientMode.TwoColors:
            {
                var min = gradient.colorMin;
                min.a *= factor;
                gradient.colorMin = min;

                var max = gradient.colorMax;
                max.a *= factor;
                gradient.colorMax = max;
                break;
            }

            case ParticleSystemGradientMode.Gradient:
            {
                gradient.gradient = ScaleGradient(gradient.gradient, factor);
                break;
            }

            case ParticleSystemGradientMode.TwoGradients:
            {
                gradient.gradientMin = ScaleGradient(gradient.gradientMin, factor);
                gradient.gradientMax = ScaleGradient(gradient.gradientMax, factor);
                break;
            }
        }

        return gradient;
    }

    private static Gradient ScaleGradient(Gradient gradient, float factor)
    {
        var alphaKeys = gradient.alphaKeys;
        for (var i = 0; i < alphaKeys.Length; i++)
        {
            alphaKeys[i].alpha *= factor;
        }

        gradient.alphaKeys = alphaKeys;
        return gradient;
    }

    private static Transform? FindChild(Transform root, string nameContains)
    {
        foreach (var transform in root.GetComponentsInChildren<Transform>(true))
        {
            if (transform != root
                && transform.name.IndexOf(nameContains, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return transform;
            }
        }

        return null;
    }

    private void ApplyExplosionDamage()
    {
        var explosion = _projectile != null ? _projectile.ExplosionChild : null;
        if (explosion == null)
        {
            return;
        }

        var damage = FlyerSummonPlugin.BombDamage.Value;
        foreach (var damager in explosion.GetComponentsInChildren<DamageEnemies>(true))
        {
            damager.damageDealt = damage;
            damager.AwardJournalKill = FlyerSummonPlugin.AwardJournalKill.Value;
        }
    }

    private static bool IsHostileEnemy(Collider2D? collider)
    {
        if (collider == null)
        {
            return false;
        }

        var health = collider.GetComponentInParent<HealthManager>();
        return health != null && !health.isDead && health.GetComponentInParent<AllyFlyer>() == null;
    }
}
