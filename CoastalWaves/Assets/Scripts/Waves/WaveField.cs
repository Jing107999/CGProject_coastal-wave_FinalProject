using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// The ocean surface as a sum of Gerstner waves, evaluated on the CPU.
    ///
    /// WHY: the wave-break detector (S2) and foam (S3) must know exactly where the
    /// water is. They can't read positions back from the GPU cheaply, so we compute
    /// the same formula here in C#. (S1.4 adds the GPU copy of this formula.)
    ///
    /// WHAT: a list of GerstnerWave settings, converted once into ready-to-use
    /// numbers (direction vector D, wavenumber k, amplitude A, angular frequency w).
    /// Evaluate(x0, z0, t) returns where the particle that rests at (x0, z0) is at
    /// time t, plus its surface normal, velocity and Jacobian.
    ///
    /// HOW: for each wave i, with phase theta_i = k_i (D_i . x0) - w_i t + phi_i:
    ///     x = x0 + sum D_i.x A_i cos(theta_i)
    ///     y =      sum       A_i sin(theta_i)
    ///     z = z0 + sum D_i.z A_i cos(theta_i)
    /// Normal, velocity and Jacobian come from the exact derivatives of these sums.
    ///
    /// S4.3 WALL REFLECTION (method of images): the cliff is approximated by a vertical plane
    /// n . x = c. For every long incoming wave heading into it, an IMAGE wave is added: direction
    /// mirrored about the plane, amplitude x reflection coefficient R, and phase chosen so both
    /// waves have the same phase on the plane. There the vertical motions add (crest x (1 + R))
    /// and the motions INTO the wall cancel (for R = 1), as at a rigid wall. The image is faded
    /// out with distance from the wall by w = exp(-max(c - n . x0, 0) / L), so the standing-wave
    /// pattern (clapotis) only exists near the cliff. w is part of the formula, so its derivative
    /// is included in the tangents: normals and the Jacobian stay exact, on CPU and GPU alike.
    /// </summary>
    [ExecuteAlways] // keep the derived numbers valid in Edit mode too (debug probes use them)
    public class WaveField : MonoBehaviour
    {
        /// <summary>Gravity used by the dispersion relation w = sqrt(g k), in m/s^2.</summary>
        public const float Gravity = 9.81f;

        /// <summary>Largest number of waves the GPU can take. Must equal MAX_WAVES in Gerstner.hlsl.</summary>
        public const int MaxWaves = 24;

        // ---------------------------------------------------------------
        // Inspector settings
        // ---------------------------------------------------------------

        [Tooltip("The wave components. Their effects are added together.")]
        public GerstnerWave[] waves = StormSea();

        [Tooltip("Multiplies every wave's steepness. Quick way to make the sea calmer or rougher.")]
        [Range(0f, 1.5f)] public float steepnessScale = 1f;

        [Tooltip("Height of the calm sea surface in world space (metres).")]
        public float seaLevel = 0f;

        [Header("Wall reflection (S4.3, method of images)")]
        public bool reflection = true;
        [Tooltip("Unit normal of the wall plane, pointing from the sea INTO the rock (x, z).")]
        public Vector2 wallNormal = new Vector2(1f, 0f);
        [Tooltip("Plane offset: wall points satisfy dot(n, x) = c. 25 m = the cliff's mean waterline.")]
        public float wallOffset = 25f;
        [Tooltip("Fraction of the wave height reflected (1 = perfect mirror).")]
        [Range(0f, 1f)] public float reflectionCoefficient = 0.5f;
        [Tooltip("Only waves at least this long are reflected; shorter ones are scattered by the rough face. " +
                 "40 m keeps the total steepness at the wall below 1 (0.913 for the storm sea).")]
        public float minReflectedWavelength = 40f;
        [Tooltip("The reflected waves fade out over this distance from the wall (metres).")]
        public float reflectionDecay = 60f;

        [Header("Debug")]
        [Tooltip("Write a summary to the Console whenever the wave set changes.")]
        public bool debugLogs = true;

        const string Tag = "[Waves] ";

        // ---------------------------------------------------------------
        // Derived numbers (computed by Rebuild, read by Evaluate)
        // ---------------------------------------------------------------

        /// <summary>One wave, converted into the numbers the formula needs.</summary>
        public struct Component
        {
            public float dx, dz;  // unit travel direction D in the XZ plane
            public float k;       // wavenumber 2*pi / L          (rad/m)
            public float a;       // amplitude A = s / k          (m)
            public float omega;   // angular frequency sqrt(g k)  (rad/s)
            public float phase;   // phi                          (rad)
            public float s;       // steepness k * A (after steepnessScale)
            public bool windowed; // S4.3: an image wave, faded out with distance from the wall
        }

        Component[] components = new Component[0];

        // GPU copies of the components, packed two float4s per wave (see Gerstner.hlsl).
        // Always MaxWaves long: Unity fixes a global array's size the first time it is set.
        readonly Vector4[] gpuA = new Vector4[MaxWaves];   // (D.x, D.z, k, A)
        readonly Vector4[] gpuB = new Vector4[MaxWaves];   // (omega, phase, s, windowed 0/1)

        // Shader property IDs (looked up once).
        static readonly int GerstnerAId = Shader.PropertyToID("_GerstnerA");
        static readonly int GerstnerBId = Shader.PropertyToID("_GerstnerB");
        static readonly int GerstnerCountId = Shader.PropertyToID("_GerstnerCount");
        static readonly int SeaLevelId = Shader.PropertyToID("_SeaLevel");
        static readonly int MaxHeightId = Shader.PropertyToID("_GerstnerMaxHeight");
        static readonly int WallPlaneId = Shader.PropertyToID("_WallPlane");

        /// <summary>The converted wave list (read-only use).</summary>
        public Component[] Components => components;

        /// <summary>Current simulation time, set by the clock every step.</summary>
        public float SimTime { get; private set; }

        /// <summary>Sum of all steepnesses. Must be below 1 for a surface that never folds.</summary>
        public float TotalSteepness { get; private set; }

        /// <summary>Sum of all amplitudes = the highest a crest could ever reach above sea level.</summary>
        public float MaxHeight { get; private set; }

        /// <summary>How many image (reflected) components were added.</summary>
        public int ReflectedCount { get; private set; }

        /// <summary>Total steepness of the incoming waves only (away from the wall).</summary>
        public float IncidentSteepness { get; private set; }

        // ---------------------------------------------------------------
        // Unity callbacks
        // ---------------------------------------------------------------

        void OnEnable() => Rebuild();

        // Inspector edit: recompute the derived numbers straight away.
        void OnValidate() => Rebuild();

        // ---------------------------------------------------------------
        // Setup
        // ---------------------------------------------------------------

        /// <summary>Convert the human settings (degrees, metres) into formula numbers.</summary>
        public void Rebuild()
        {
            if (waves == null) waves = new GerstnerWave[0];
            if (waves.Length > MaxWaves)
                Debug.LogWarning(Tag + $"{waves.Length} waves, but the GPU takes at most {MaxWaves}; " +
                                 "the extra ones are ignored on BOTH CPU and GPU so they stay identical.", this);
            int incident = Mathf.Min(waves.Length, MaxWaves);
            var list = new System.Collections.Generic.List<Component>(MaxWaves);
            TotalSteepness = 0f;
            MaxHeight = 0f;
            ReflectedCount = 0;

            for (int i = 0; i < incident; i++)
            {
                GerstnerWave w = waves[i];
                float rad = w.directionDeg * Mathf.Deg2Rad;
                float k = 2f * Mathf.PI / Mathf.Max(0.5f, w.wavelength);
                float s = Mathf.Clamp01(w.steepness * steepnessScale);

                list.Add(new Component
                {
                    dx = Mathf.Cos(rad),        // direction angle -> unit vector
                    dz = Mathf.Sin(rad),
                    k = k,
                    a = s / k,                  // from s = k A
                    omega = Mathf.Sqrt(Gravity * k), // deep-water dispersion
                    phase = w.phase,
                    s = s,
                });
                TotalSteepness += s;
                MaxHeight += s / k;
            }
            IncidentSteepness = TotalSteepness;

            // S4.3: image waves for the long components that travel INTO the wall.
            if (reflection && reflectionCoefficient > 0f)
            {
                Vector2 n = wallNormal.sqrMagnitude > 1e-6f ? wallNormal.normalized : Vector2.right;
                for (int i = 0; i < incident && list.Count < MaxWaves; i++)
                {
                    Component c = list[i];
                    float dn = c.dx * n.x + c.dz * n.y;                       // D . n (> 0: heading into the wall)
                    if (dn <= 0f || 2f * Mathf.PI / c.k < minReflectedWavelength) continue;
                    list.Add(new Component
                    {
                        dx = c.dx - 2f * dn * n.x,                            // mirror the direction
                        dz = c.dz - 2f * dn * n.y,
                        k = c.k,
                        a = c.a * reflectionCoefficient,
                        omega = c.omega,
                        // Same phase as the incident wave on the plane n.x = c (see the summary).
                        phase = c.phase + 2f * c.k * dn * wallOffset,
                        s = c.s * reflectionCoefficient,
                        windowed = true,
                    });
                    TotalSteepness += c.s * reflectionCoefficient;              // worst case: AT the wall (w = 1)
                    MaxHeight += c.a * reflectionCoefficient;
                    ReflectedCount++;
                }
            }
            components = list.ToArray();

            if (debugLogs)
            {
                string verdict = TotalSteepness < 1f ? "OK, surface cannot fold" : "WARNING: >= 1, surface may fold into loops";
                Debug.Log(Tag + $"{components.Length} waves ({ReflectedCount} reflected), total steepness = " +
                          $"{TotalSteepness:F3} at the wall, {IncidentSteepness:F3} away from it ({verdict}), " +
                          $"max crest height = {MaxHeight:F2} m", this);
            }

            Upload(); // keep the GPU copy in sync with every change
        }

        /// <summary>
        /// Send the converted waves to every shader as global properties.
        /// Gerstner.hlsl reads exactly these numbers, so CPU and GPU evaluate the same sea.
        /// </summary>
        public void Upload()
        {
            for (int i = 0; i < MaxWaves; i++)
            {
                if (i < components.Length)
                {
                    Component c = components[i];
                    gpuA[i] = new Vector4(c.dx, c.dz, c.k, c.a);
                    gpuB[i] = new Vector4(c.omega, c.phase, c.s, c.windowed ? 1f : 0f);
                }
                else
                {
                    gpuA[i] = gpuB[i] = Vector4.zero; // unused slots
                }
            }

            Shader.SetGlobalVectorArray(GerstnerAId, gpuA);
            Shader.SetGlobalVectorArray(GerstnerBId, gpuB);
            Shader.SetGlobalFloat(GerstnerCountId, components.Length);
            Shader.SetGlobalFloat(SeaLevelId, seaLevel);
            Shader.SetGlobalFloat(MaxHeightId, Mathf.Max(0.01f, MaxHeight));
            Vector2 wn = wallNormal.sqrMagnitude > 1e-6f ? wallNormal.normalized : Vector2.right;
            Shader.SetGlobalVector(WallPlaneId, new Vector4(wn.x, wn.y, wallOffset, 1f / Mathf.Max(1f, reflectionDecay)));
        }

        /// <summary>
        /// Fade factor w of the image waves at rest position (x0, z0): 1 on and behind the wall,
        /// exp(-distance / L) out at sea. Also returns its gradient (dw/dx0, dw/dz0).
        /// </summary>
        public float WallWindow(float x0, float z0, out float gx, out float gz)
        {
            Vector2 n = wallNormal.sqrMagnitude > 1e-6f ? wallNormal.normalized : Vector2.right;
            float dist = wallOffset - (n.x * x0 + n.y * z0);        // > 0 = out at sea
            float invL = 1f / Mathf.Max(1f, reflectionDecay);
            float w = Mathf.Exp(-Mathf.Max(dist, 0f) * invL);
            // d(dist)/dx0 = -n.x, so dw/dx0 = w * n.x / L at sea; 0 behind the wall (w is constant 1 there).
            gx = dist > 0f ? w * invL * n.x : 0f;
            gz = dist > 0f ? w * invL * n.y : 0f;
            return w;
        }

        /// <summary>Called by the clock every step, so every query uses the same time.</summary>
        public void SetTime(float t) => SimTime = t;

        // ---------------------------------------------------------------
        // The wave formula
        // ---------------------------------------------------------------

        /// <summary>
        /// Where is the water particle that rests at (x0, z0) at time t?
        /// Also returns its surface normal, velocity and Jacobian.
        /// </summary>
        public WaveSample Evaluate(float x0, float z0, float t)
        {
            // Start from the particle's rest position on the calm sea.
            float px = x0, py = seaLevel, pz = z0;

            // Two tangent vectors lying IN the surface: how the position changes if we
            // move the rest point a little along X (tx) or along Z (tz). On a flat sea
            // they are (1,0,0) and (0,0,1); every wave bends them.
            float txx = 1f, txy = 0f, txz = 0f;
            float tzx = 0f, tzy = 0f, tzz = 1f;

            // Velocity = how the position changes with time.
            float vx = 0f, vy = 0f, vz = 0f;

            // S4.3: fade factor of the image waves at this particle (1 for ordinary waves).
            float window = WallWindow(x0, z0, out float wgx, out float wgz);

            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                float w = c.windowed ? window : 1f;          // this wave's local strength
                float gx = c.windowed ? wgx : 0f;            // ... and how it changes along x0, z0
                float gz = c.windowed ? wgz : 0f;
                float a = c.a * w, s = c.s * w;

                // Where this particle is in this wave's cycle.
                float theta = c.k * (c.dx * x0 + c.dz * z0) - c.omega * t + c.phase;
                float sin = Mathf.Sin(theta);
                float cos = Mathf.Cos(theta);

                // POSITION: move around a circle of radius A (times the fade).
                px += c.dx * a * cos;
                py += a * sin;
                pz += c.dz * a * cos;

                // TANGENTS: derivative of the position w.r.t. x0 and z0.
                // d(theta)/dx0 = k dx, and k*A = s, so the wave terms carry s. A faded wave adds
                // the change of its amplitude, c.a * (dw/dx0), times the unchanged cos/sin shape.
                txx += -s * c.dx * c.dx * sin + c.a * gx * c.dx * cos;
                txy +=  s * c.dx * cos        + c.a * gx * sin;
                txz += -s * c.dx * c.dz * sin + c.a * gx * c.dz * cos;

                tzx += -s * c.dx * c.dz * sin + c.a * gz * c.dx * cos;
                tzy +=  s * c.dz * cos        + c.a * gz * sin;
                tzz += -s * c.dz * c.dz * sin + c.a * gz * c.dz * cos;

                // VELOCITY: derivative w.r.t. time. d(theta)/dt = -omega.
                float aw = a * c.omega; // orbital speed of this wave
                vx += c.dx * aw * sin;
                vy -= aw * cos;
                vz += c.dz * aw * sin;
            }

            var tangentX = new Vector3(txx, txy, txz);
            var tangentZ = new Vector3(tzx, tzy, tzz);

            return new WaveSample
            {
                position = new Vector3(px, py, pz),
                // Cross product of the two tangents = vector perpendicular to the surface.
                // Order (tz x tx) makes it point up rather than down.
                normal = Vector3.Cross(tangentZ, tangentX).normalized,
                velocity = new Vector3(vx, vy, vz),
                // Area change of the horizontal map: determinant of the 2x2 XZ part.
                jacobian = txx * tzz - txz * tzx,
            };
        }

        // ---------------------------------------------------------------
        // Queries at a FIXED world point (S1.5)
        // ---------------------------------------------------------------
        //
        // Evaluate() answers "where is the particle that RESTS at x0?" (Lagrangian).
        // Wave-break detection needs "how high is the water HERE?" at a fixed world
        // point p (Eulerian). Particles move sideways, so the particle above p started
        // somewhere else. We find its rest position x0 by solving
        //     x0 + H(x0) = p        (H = horizontal Gerstner shift)
        // with fixed-point iteration  x0 <- p - H(x0), starting from x0 = p.
        // It converges because |dH/dx0| <= total steepness < 1 (a contraction).

        /// <summary>
        /// Number of inversion iterations used when the caller doesn't specify one.
        /// Chosen by experiment E1 (S1.5): the smallest n with 95th-percentile height
        /// error < 1 mm and max < 1 cm at total steepness 0.755 (measured 0.41 mm / 4.2 mm).
        /// Steeper seas need more: at total steepness 0.94, n = 8.
        /// S4.3: wall reflection raises the steepness AT the wall to 0.912. Measured there (6000 points,
        /// x 5..25 m, vs 40 iterations): n = 6 gives max 29 mm (fails), n = 8 gives p95 0.08 mm /
        /// max 9.8 mm (passes). So the default is now 8.
        /// </summary>
        public const int DefaultInversionIterations = 8;

        /// <summary>
        /// Horizontal shift H(x0) of the particle resting at (x0, z0): only the cosine
        /// terms of the position formula (cheaper than a full Evaluate).
        /// </summary>
        public Vector2 HorizontalShift(float x0, float z0, float t)
        {
            float hx = 0f, hz = 0f;
            float window = WallWindow(x0, z0, out _, out _);
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                float theta = c.k * (c.dx * x0 + c.dz * z0) - c.omega * t + c.phase;
                float shift = c.a * (c.windowed ? window : 1f) * Mathf.Cos(theta);
                hx += c.dx * shift;
                hz += c.dz * shift;
            }
            return new Vector2(hx, hz);
        }

        /// <summary>
        /// Rest position (x0, z0) whose particle is at world (x, z) at time t, after
        /// 'iterations' rounds of fixed-point iteration. 0 iterations = the naive guess x0 = p.
        /// </summary>
        public Vector2 FindRestPosition(float x, float z, float t, int iterations)
        {
            float x0 = x, z0 = z;                       // first guess: no sideways shift
            for (int it = 0; it < iterations; it++)
            {
                Vector2 h = HorizontalShift(x0, z0, t); // shift this guess would cause
                x0 = x - h.x;                           // correct the guess by it
                z0 = z - h.y;
            }
            return new Vector2(x0, z0);
        }

        /// <summary>
        /// Everything about the water at the FIXED world point (x, z): position (y = water
        /// height), normal, velocity and Jacobian of the particle that is there now.
        /// </summary>
        public WaveSample SampleAt(float x, float z, float t, int iterations = DefaultInversionIterations)
        {
            Vector2 rest = FindRestPosition(x, z, t, iterations);
            return Evaluate(rest.x, rest.y, t);
        }

        /// <summary>World-space height (y) of the water surface at the fixed point (x, z).</summary>
        public float HeightAt(float x, float z, float t, int iterations = DefaultInversionIterations)
            => SampleAt(x, z, t, iterations).position.y;

        // ---------------------------------------------------------------
        // Presets (right-click the component header to use them)
        // ---------------------------------------------------------------

        /// <summary>
        /// Default storm sea: 10 components. Long waves carry the height and travel
        /// roughly towards +X (the rock); short waves add texture from wider angles.
        /// Total steepness 0.755, max crest 4.26 m.
        /// </summary>
        public static GerstnerWave[] StormSea() => new[]
        {
            //               direction°  wavelength m  steepness  phase rad
            new GerstnerWave(   0f,      70f,          0.170f,    0.0f),
            new GerstnerWave(  12f,      48f,          0.145f,    1.3f),
            new GerstnerWave( -18f,      33f,          0.120f,    2.1f),
            new GerstnerWave(  28f,      22f,          0.095f,    0.7f),
            new GerstnerWave( -30f,      14f,          0.070f,    4.0f),
            new GerstnerWave(  40f,       9f,          0.050f,    5.2f),
            new GerstnerWave( -15f,       6f,          0.040f,    3.3f),
            new GerstnerWave(  55f,       4f,          0.030f,    1.9f),
            new GerstnerWave( -45f,       2.6f,        0.020f,    0.4f),
            new GerstnerWave(  20f,       1.8f,        0.015f,    2.8f),
        };

        /// <summary>
        /// A single wave for checking the maths by hand: L = 20 m, s = 0.5, along +X.
        /// Expected: orbit radius A = 1.59 m, period T = 3.58 s, crest speed c = 5.59 m/s.
        /// </summary>
        public static GerstnerWave[] TestWave() => new[] { new GerstnerWave(0f, 20f, 0.5f, 0f) };

        [ContextMenu("Use Storm Sea (10 waves)")]
        public void UseStormSea() { waves = StormSea(); steepnessScale = 1f; Rebuild(); }

        [ContextMenu("Use Test Wave (L = 20 m, s = 0.5)")]
        public void UseTestWave() { waves = TestWave(); steepnessScale = 1f; Rebuild(); }
    }
}
