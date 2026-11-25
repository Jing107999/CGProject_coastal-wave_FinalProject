using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Coastal
{
    /// <summary>
    /// Spray thrown up where a wave hits the cliff (S4.5), in LAYERS (S4.6b).
    ///
    /// Every wave-break event emits a burst into each layer. A stronger hit (higher intensity) emits
    /// more particles, faster and bigger. Each layer is its own Unity particle system, set up from code
    /// here, with its own look and motion:
    ///   Droplets  small fast drops on ballistic paths (our Coastal/Spray shader)
    ///   Splash    chunks of churning water (Unity's water_churn 8x4 flipbook)
    ///   Plume     big soft puffs of fine spray: thrown up, slowed by gravity, then sink back down at a
    ///             low "terminal" speed because air drag holds small droplets back (cloud texture)
    ///   Mist      the finest spray: very large, faint, long-lived haze that drifts with the wind and
    ///             slowly settles (cloud texture)
    /// Spray is WATER, not hot air: every layer falls. Fine droplets only fall slowly (terminal
    /// velocity of ~0.3-4 m/s for 0.1-1 mm drops), which is why spray looks like smoke from far away.
    /// Splash/Plume/Mist use URP's built-in Particles/Unlit shader with SOFT PARTICLES (they fade where
    /// they touch the rock or the water, so no hard edges). The textures come from Unity's Shader Graph
    /// samples (see Textures/ThirdParty/UnityShaderGraphSamples/README.md).
    ///
    /// The particle systems run on Unity's frame clock; this script keeps them in step with OUR clock:
    /// paused with it, at its time scale, cleared when it restarts.
    /// </summary>
    public class SprayEmitter : MonoBehaviour
    {
        [Serializable]
        public class Layer
        {
            public string name = "Layer";
            public Material material;
            [Tooltip("Particles for the weakest and for a full-intensity hit.")]
            public Vector2Int count = new Vector2Int(10, 60);
            [Tooltip("Launch speed range (m/s); stronger hits pick from higher up.")]
            public Vector2 speed = new Vector2(3f, 11f);
            [Tooltip("0 = straight out to sea, 1 = straight up.")]
            [Range(0f, 1f)] public float upwardBias = 0.75f;
            [Range(0f, 80f)] public float coneAngle = 35f;
            [Tooltip("Start size range (m).")]
            public Vector2 size = new Vector2(0.1f, 0.5f);
            [Tooltip("Size at the end of the life, relative to the start (puffs spread out).")]
            public float growth = 1.4f;
            public Vector2 lifetime = new Vector2(1.2f, 2.4f);
            [Tooltip("Gravity multiplier: 1 = falls like a stone, ~0 = floats, < 0 = rises.")]
            public float gravity = 1f;
            [Tooltip("Opacity at the start (fades to 0 at the end).")]
            [Range(0f, 1f)] public float alpha = 0.9f;
            public Color tint = new Color(0.92f, 0.94f, 0.96f, 1f);
            [Tooltip("Air drag: how strongly speed above 'dragLimit' is damped (0 = none).")]
            [Range(0f, 1f)] public float drag = 0f;
            [Tooltip("Speed the drag holds particles to (m/s): for fine spray, the terminal fall speed.")]
            public float dragLimit = 0.5f;
            [Tooltip("Speed limit right after launch (m/s). The limit eases from this down to 'dragLimit' over " +
                     "the first 35 % of the life: the impact's momentum throws the spray up first, then drag takes over.")]
            public float dragLimitStart = 0f;
            [Tooltip("Turbulence (billowing): strength in m/s and frequency.")]
            public float noiseStrength = 0f, noiseFrequency = 0.3f;
            [Tooltip("Constant acceleration, e.g. wind (m/s^2).")]
            public Vector3 wind = Vector3.zero;
            [Tooltip("Flipbook tiles (columns, rows); (0, 0) = plain texture.")]
            public Vector2Int flipbook = Vector2Int.zero;
            [Tooltip("Remove particles that fall back to sea level.")]
            public bool killAtSea = true;
            public int maxParticles = 3000;

            [NonSerialized] public ParticleSystem system;
        }

        public WaveBreakDetector detector;
        public CoastalSimulation simulation;
        [Tooltip("Particles that fall back to this height (the calm sea) disappear.")]
        public float seaLevel = 0f;

        public Layer[] layers = DefaultLayers();

        [Header("Splash-back foam (S4.11)")]
        [Tooltip("Where the spray falls back into the sea, it leaves a foam patch (drifts and fades like the rest).")]
        public bool splashBackFoam = true;
        [Tooltip("Foam map to splat into. Found automatically if left empty.")]
        public FoamSystem foam;
        [Tooltip("Splash-back patch size relative to an impact splat of the same intensity.")]
        public float splashRadiusScale = 0.6f;
        [Tooltip("Splash-back patch strength relative to an impact splat of the same intensity.")]
        public float splashStrengthScale = 0.5f;

        /// <summary>Splash-back patches laid since start or the last restart (for the HUD / tests).</summary>
        public int SplashBackSplats { get; private set; }
        /// <summary>Bursts whose farthest landing droplet was found (some bursts have none: all droplets
        /// expire in the air before they reach the sea).</summary>
        public int SplashBackBursts { get; private set; }

        // A patch waiting for its droplet to land: released when OUR clock reaches 'time'.
        struct PendingSplash { public float time; public Vector2 xz; public float radius, strength; }
        readonly System.Collections.Generic.List<PendingSplash> pendingSplashes = new System.Collections.Generic.List<PendingSplash>();

        /// <summary>Particles emitted (all layers) since start or the last restart.</summary>
        public int TotalEmitted { get; private set; }
        /// <summary>Bursts (events) since start or the last restart.</summary>
        public int TotalBursts { get; private set; }
        /// <summary>Particles alive now, all layers.</summary>
        public int AliveCount
        {
            get { int n = 0; foreach (Layer l in layers) if (l.system != null) n += l.system.particleCount; return n; }
        }

        float lastSimTime;

        /// <summary>The four layers with their default settings (materials are assigned by the scene setup).</summary>
        public static Layer[] DefaultLayers() => new[]
        {
            new Layer { name = "Droplets", count = new Vector2Int(10, 60), speed = new Vector2(4f, 12f), upwardBias = 0.7f,
                        coneAngle = 35f, size = new Vector2(0.1f, 0.45f), growth = 1.2f, lifetime = new Vector2(1.0f, 2.0f),
                        gravity = 1f, alpha = 0.9f },
            new Layer { name = "Splash", count = new Vector2Int(4, 24), speed = new Vector2(3f, 9f), upwardBias = 0.65f,
                        coneAngle = 45f, size = new Vector2(1.0f, 3.5f), growth = 1.6f, lifetime = new Vector2(0.6f, 1.3f),
                        gravity = 0.8f, alpha = 0.85f, flipbook = new Vector2Int(8, 4) },
            // Plume: thrown up at 3-10 m/s with full gravity. Drag lets the launch through (limit 12 m/s)
            // and tightens to 2.5 m/s, so it climbs, stops, and sinks back slowly (fine spray), spreading.
            new Layer { name = "Plume", count = new Vector2Int(3, 14), speed = new Vector2(3f, 10f), upwardBias = 0.85f,
                        coneAngle = 30f, size = new Vector2(3f, 7f), growth = 2.2f, lifetime = new Vector2(3f, 5f),
                        gravity = 1f, alpha = 0.45f, drag = 0.35f, dragLimit = 2.5f, dragLimitStart = 12f,
                        noiseStrength = 0.4f, noiseFrequency = 0.3f, killAtSea = true },
            // Mist: the finest droplets. A small lift from the impact, then they settle at <= ~0.6 m/s
            // and drift sideways with the wind (horizontal only: wind doesn't lift water).
            new Layer { name = "Mist", count = new Vector2Int(1, 6), speed = new Vector2(1f, 4f), upwardBias = 0.8f,
                        coneAngle = 60f, size = new Vector2(8f, 16f), growth = 1.8f, lifetime = new Vector2(6f, 10f),
                        gravity = 0.3f, alpha = 0.18f, drag = 0.35f, dragLimit = 0.6f, dragLimitStart = 4f,
                        noiseStrength = 0.25f, noiseFrequency = 0.1f,
                        wind = new Vector3(-0.15f, 0f, 0.25f), killAtSea = true, maxParticles = 600 },
        };

        void Start()
        {
            if (foam == null) foam = FindAnyObjectByType<FoamSystem>();
        }

        /// <summary>
        /// S4.11: where a purely ballistic droplet (gravity only) launched from p with velocity v meets
        /// the plane y = seaLevel, and after how long. Solves p.y + v.y t - g t^2 / 2 = seaLevel for the
        /// later root. Returns false if it never gets there (e.g. starts below the sea).
        /// </summary>
        public static bool PredictLanding(Vector3 p, Vector3 v, float g, float seaLevel, out Vector2 landXZ, out float t)
        {
            landXZ = default; t = 0f;
            float h = p.y - seaLevel;                     // height above the sea
            float disc = v.y * v.y + 2f * g * h;
            if (g <= 0f || disc < 0f) return false;
            t = (v.y + Mathf.Sqrt(disc)) / g;             // the positive root: on the way down
            if (t <= 0f) return false;
            landXZ = new Vector2(p.x + v.x * t, p.z + v.z * t);
            return true;
        }

        void Awake()
        {
            foreach (Layer layer in layers) layer.system = CreateSystem(layer);
        }

        void OnEnable() { if (detector != null) detector.WaveBreak += OnWaveBreak; }
        void OnDisable() { if (detector != null) detector.WaveBreak -= OnWaveBreak; }

        /// <summary>A child object with a particle system configured for one layer.</summary>
        ParticleSystem CreateSystem(Layer layer)
        {
            var go = new GameObject(layer.name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);   // configure before it runs

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;   // particles stay where they were thrown
            main.gravityModifier = layer.gravity;
            main.maxParticles = layer.maxParticles;
            main.startSpeed = 0f;                                         // velocity is set per particle
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);   // no two puffs alike

            var emission = ps.emission; emission.enabled = false;         // only scripted bursts
            var shape = ps.shape; shape.enabled = false;

            // Fade out over the lifetime; puffs swell as they spread.
            var colour = ps.colorOverLifetime;
            colour.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(new[] { new GradientColorKey(layer.tint, 0f), new GradientColorKey(layer.tint, 1f) },
                         new[] { new GradientAlphaKey(layer.alpha, 0f), new GradientAlphaKey(layer.alpha * 0.7f, 0.4f),
                                 new GradientAlphaKey(0f, 1f) });
            colour.color = fade;
            var grow = ps.sizeOverLifetime;
            grow.enabled = true;
            grow.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, layer.growth));

            if (layer.drag > 0f)   // air drag: the plume shoots up, then slows and hangs
            {
                var limit = ps.limitVelocityOverLifetime;
                limit.enabled = true;
                if (layer.dragLimitStart > layer.dragLimit)
                {
                    // Fast at first (momentum of the impact), then held to the slow terminal speed.
                    var curve = new AnimationCurve(new Keyframe(0f, layer.dragLimitStart), new Keyframe(0.35f, layer.dragLimit),
                                                   new Keyframe(1f, layer.dragLimit));
                    limit.limit = new ParticleSystem.MinMaxCurve(1f, curve);
                }
                else limit.limit = layer.dragLimit;
                limit.dampen = layer.drag;
            }
            if (layer.noiseStrength > 0f)   // turbulence: billowing, not straight lines
            {
                var noise = ps.noise;
                noise.enabled = true;
                noise.strength = layer.noiseStrength;
                noise.frequency = layer.noiseFrequency;
                noise.scrollSpeed = 0.2f;
                noise.damping = true;
            }
            if (layer.wind != Vector3.zero)
            {
                var force = ps.forceOverLifetime;
                force.enabled = true;
                force.space = ParticleSystemSimulationSpace.World;
                force.x = layer.wind.x; force.y = layer.wind.y; force.z = layer.wind.z;
            }
            if (layer.flipbook.x > 0 && layer.flipbook.y > 0)   // animated sprite sheet over the lifetime
            {
                var sheet = ps.textureSheetAnimation;
                sheet.enabled = true;
                sheet.numTilesX = layer.flipbook.x;
                sheet.numTilesY = layer.flipbook.y;
                sheet.animation = ParticleSystemAnimationType.WholeSheet;
            }
            if (layer.killAtSea)   // particles that fall back into the sea disappear
            {
                var sea = new GameObject("SeaPlane").transform;
                sea.SetParent(go.transform, false);
                sea.position = new Vector3(0f, seaLevel, 0f);   // a plane's normal is its up axis: +y
                var collision = ps.collision;
                collision.enabled = true;
                collision.type = ParticleSystemCollisionType.Planes;
                collision.SetPlane(0, sea);
                collision.lifetimeLoss = 1f;
                collision.bounce = 0f;
                // Collide with the particle's CENTRE, not its size: a 10 m mist puff launched at 3 m
                // would otherwise "touch" the sea immediately and die (found in S4.6 run 2).
                collision.radiusScale = 0.001f;
            }

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.sharedMaterial = layer.material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.sortingFudge = layer.name == "Mist" ? 10f : 0f;   // draw the big haze behind the rest

            ps.Play();
            return ps;
        }

        void Update()
        {
            if (simulation == null) return;
            // Follow OUR clock: frozen when paused, faster/slower with the time scale.
            float speed = simulation.paused ? 0f : simulation.timeScale;
            bool restarted = simulation.SimTime < lastSimTime;
            foreach (Layer layer in layers)
            {
                if (layer.system == null) continue;
                var main = layer.system.main;
                main.simulationSpeed = speed;
                if (restarted) layer.system.Clear();
            }
            if (restarted) { TotalEmitted = 0; TotalBursts = 0; SplashBackSplats = 0; SplashBackBursts = 0; pendingSplashes.Clear(); }
            lastSimTime = simulation.SimTime;

            // S4.11: release the splash-back patches whose droplet has now landed (on OUR clock, so
            // pause and time scale apply; a restart cleared the queue above).
            for (int i = pendingSplashes.Count - 1; i >= 0; i--)
            {
                PendingSplash s = pendingSplashes[i];
                if (s.time > simulation.SimTime) continue;
                if (foam != null) { foam.AddSplatAtWorldPoint(s.xz, s.radius, s.strength); SplashBackSplats++; }
                pendingSplashes.RemoveAt(i);
            }
        }

        void OnWaveBreak(WaveBreakEvent e)
        {
            float I = Mathf.Clamp01(e.intensity);
            Vector3 outward = new Vector3(e.outward.x, 0f, e.outward.z).normalized;
            farthestFound = false;   // set by Emit for the Droplets layer (S4.11)
            foreach (Layer layer in layers)
                if (layer.system != null) TotalEmitted += Emit(layer, e.position, outward, I);
            TotalBursts++;

            // S4.11: one splash-back patch where this burst's farthest droplet lands (found in Emit).
            if (splashBackFoam && foam != null && simulation != null && farthestFound)
            {
                pendingSplashes.Add(new PendingSplash
                {
                    time = simulation.SimTime + farthestTime,
                    xz = farthestXZ,
                    radius = foam.splatRadius * (0.6f + 0.8f * I) * splashRadiusScale,   // same scaling as impacts
                    strength = foam.splatStrength * I * splashStrengthScale,
                });
                SplashBackBursts++;
            }
        }

        // The farthest-landing droplet of the burst being emitted (S4.11).
        bool farthestFound;
        Vector2 farthestXZ;
        float farthestTime, farthestReach;

        int Emit(Layer layer, Vector3 position, Vector3 outward, float I)
        {
            int n = Mathf.RoundToInt(Mathf.Lerp(layer.count.x, layer.count.y, I));
            // Main direction: mostly up, partly out to sea (away from the rock face).
            Vector3 main = Vector3.Slerp(outward, Vector3.up, layer.upwardBias);
            // Any axis perpendicular to 'main' (to tilt away from it); avoid the one parallel to it.
            Vector3 tiltAxis = Vector3.Cross(main, Mathf.Abs(main.y) < 0.99f ? Vector3.up : Vector3.right).normalized;

            // S4.11: only the Droplets layer is purely ballistic (gravity only: no drag, turbulence or
            // wind), so only its landing points can be predicted exactly. Reset the search per burst.
            bool ballistic = layer.name == "Droplets" && layer.drag <= 0f && layer.noiseStrength <= 0f && layer.wind == Vector3.zero;
            if (ballistic) { farthestFound = false; farthestReach = float.MinValue; }
            float g = -Physics.gravity.y * layer.gravity;   // Unity's gravity times the layer's modifier

            var emit = new ParticleSystem.EmitParams();
            for (int i = 0; i < n; i++)
            {
                // Random direction inside the cone around 'main': tilt by up to coneAngle, then spin.
                Vector3 dir = Quaternion.AngleAxis(Random.Range(0f, 360f), main)
                            * Quaternion.AngleAxis(Random.Range(0f, layer.coneAngle), tiltAxis) * main;
                float v = Mathf.Lerp(layer.speed.x, layer.speed.y, Mathf.Lerp(0.2f, 1f, I) * Random.Range(0.6f, 1f));
                emit.position = position + outward * 0.5f;
                emit.velocity = dir.normalized * v;
                emit.startSize = Mathf.Lerp(layer.size.x, layer.size.y, Random.value * Mathf.Lerp(0.4f, 1f, I));
                emit.startLifetime = Random.Range(layer.lifetime.x, layer.lifetime.y);
                layer.system.Emit(emit, 1);

                // S4.11: does this droplet land before it expires, and is it the farthest out to sea so far?
                if (ballistic && PredictLanding(emit.position, emit.velocity, g, seaLevel, out Vector2 land, out float t)
                    && t <= emit.startLifetime)
                {
                    float reach = Vector2.Dot(land - new Vector2(position.x, position.z), new Vector2(outward.x, outward.z));
                    if (reach > farthestReach) { farthestReach = reach; farthestXZ = land; farthestTime = t; farthestFound = true; }
                }
            }
            return n;
        }
    }
}
