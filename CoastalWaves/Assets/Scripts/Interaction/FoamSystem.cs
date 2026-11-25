using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Coastal
{
    /// <summary>
    /// Foam on the water around the cliff, stored as a density map on the GPU.
    ///
    /// The map is a float texture lying flat over the coast (world X -> u, world Z -> v).
    /// Every simulation step the clock calls Tick(dt), which runs FoamUpdate.shader once:
    ///   new map = exp(-dt / tau) * old map + Gaussian splats queued since the last step.
    /// Two textures take turns being "old" and "new" (ping-pong).
    ///
    /// S3.1: decay + manual splats, a screen preview and a one-texel readback to measure the fade.
    /// S3.2: every wave-break event adds a splat just in front of the rock; stronger hits make
    ///       bigger, brighter splats.
    /// S3.3: foam spreads (diffusion, D in m^2/s), so a splat widens while it fades.
    /// The water shader reads the map (_FoamTex, _FoamRect) in S3.4.
    ///
    /// S5.2 FOAM RIDES THE WATER: the map is indexed by REST position (where a water particle sits
    /// on the calm sea), not by world position. The water shader looks foam up at each pixel's rest
    /// position, so a patch stays on the same particles and is drawn wherever the Gerstner formula
    /// moves them: towards the rock under a crest, back out with the reflected waves under a trough.
    /// Splats from impacts are converted from the world point to the rest position of the particle
    /// that is there now. The drift now moves rest positions, i.e. it is the particles' mean drift.
    /// </summary>
    public class FoamSystem : MonoBehaviour
    {
        const int MaxSplatsPerStep = 64;   // must match MAX_SPLATS in FoamUpdate.shader
        const float MaxAlpha = 0.9f;       // diffusion weight per pass; above 1 the scheme goes unstable
        const int MaxSubsteps = 16;        // safety cap (only reached for a huge D or a very long frame)

        [Tooltip("Hidden/Coastal/FoamUpdate: one simulation step of the map.")]
        public Shader updateShader;
        [Tooltip("Its wave-break events add foam.")]
        public WaveBreakDetector detector;

        [Header("Map")]
        [Tooltip("World position (x, z) of the map's centre.")]
        public Vector2 centre = new Vector2(10f, 0f);
        [Tooltip("World size of the map in metres (x, z). Covers the whole cliff and the water in front of it.")]
        // S4.10b: 400 m along the coast (was 260 m), so foam reaches both ends of the cliff.
        public Vector2 worldSize = new Vector2(100f, 400f);
        [Tooltip("Texels along the longer side; the other side gets the same texel size.")]
        public int resolution = 1576;   // keeps ~0.25 m texels over the longer map

        [Header("Decay")]
        [Tooltip("Foam lifetime tau (seconds): after tau seconds, foam is down to 37 % (e^-1).")]
        public float decayTime = 10f;  // S5.5: was 5 s (S4.10e); 10 s keeps the cliff foot white between hits (90 % vs 72 %)

        [Header("Diffusion (S3.3)")]
        [Tooltip("How fast foam spreads, in m^2/s. A splat of radius r0 grows as r^2 = r0^2 + 4 D t: " +
                 "with 0.34, a 2.2 m splat is ~3.2 m wide after 4 s.")]
        [Min(0f)] public float diffusion = 0.34f;

        [Header("Whitecap trails and drift (S4.8)")]
        [Tooltip("Foam added per second on crests steep enough to break, per unit of (bias - J). 0 = off. " +
                 "The moving crest leaves a trail that fades with tau and spreads with D.")]
        [Min(0f)] public float whitecapRate = 0.12f;  // S4.8: calibrated to ~1 % trail cover (1.6 gave 8 %, 0.2 gave 1 %).
                                                     // S5.2: foam now sits on the water, and a crest squeezes its water (J ~ 0.6),
                                                     // so the same rate gave 1.7x the cover; 0.12 restores ~1 %.
        [Tooltip("Crests with surface Jacobian J below this make foam. 0.71 = calibrated to observed " +
                 "whitecap coverage in S4.2 (same value as the water shader's crest foam).")]
        public float whitecapBias = 0.71f;
        [Tooltip("Foam is carried by the swell (Stokes drift), in m/s (x, z). (0, 0) = off.")]
        public Vector2 drift = new Vector2(0.4f, 0.1f);

        [Header("Splats from wave breaks (S3.2)")]
        [Tooltip("Splat radius (m) for intensity 0.5; it scales from 0.6x (weak) to 1.4x (full hit).")]
        public float splatRadius = 3f;     // S4.10e: was 2.2 m; 3 m gives an ~11 m foam band (E4: 5 m gave 19 m, too wide)
        [Tooltip("Foam added at the splat centre by a full-intensity hit.")]
        public float splatStrength = 0.9f; // S4.10e: was 0.55; centres stay above the drawing floor further out
        [Tooltip("The splat sits this far out from the rock (m), where the water hits.")]
        public float splatOutset = 1.5f;   // S4.10e: was 1 m; less of the patch hidden by contact foam and spray

        [Header("Test splat (right-click the component > Add Test Splat)")]
        [Tooltip("REST position (x0, z0) of the water the test foam is put on (S5.2); it then moves with that water.")]
        public Vector2 testSplatPosition = new Vector2(0f, 0f);
        public float testSplatRadius = 2.2f;
        public float testSplatStrength = 1f;

        [Header("Debug")]
        [Tooltip("Draw the foam map in the bottom-right corner of the Game view (red = foam).")]
        public bool showPreview = true;
        [Tooltip("Record the value at one world point every step (for the decay measurement).")]
        public bool recordTrace = false;
        public bool debugLogs = true;
        const string Tag = "[Foam] ";

        /// <summary>The current foam map (for the water shader and debug views).</summary>
        public RenderTexture FoamTexture => current;
        /// <summary>How many splats went into the last step.</summary>
        public int SplatsLastStep { get; private set; }
        /// <summary>How many shader passes the last step needed (more than 1 = the frame was split for stability).</summary>
        public int SubstepsLastStep { get; private set; }
        /// <summary>Splats queued since the last Clear() (from events and by hand).</summary>
        public int TotalSplats { get; private set; }
        /// <summary>Splats thrown away because more than 64 arrived in one step.</summary>
        public int DroppedSplats { get; private set; }

        RenderTexture current, previous;
        Material material;
        readonly List<Vector4> pending = new List<Vector4>();
        readonly Vector4[] splatArray = new Vector4[MaxSplatsPerStep];

        // Readback of one texel (debug / measurement).
        Vector2Int traceTexel = new Vector2Int(-1, -1);
        readonly List<Vector2> trace = new List<Vector2>();   // (sim time, foam value)
        float simTime;
        int generation;   // +1 on every Clear(), so readbacks still in flight from before are ignored

        static readonly int FoamTexId = Shader.PropertyToID("_FoamTex");
        static readonly int FoamRectId = Shader.PropertyToID("_FoamRect");
        static readonly int SplatsId = Shader.PropertyToID("_Splats");
        static readonly int SplatCountId = Shader.PropertyToID("_SplatCount");
        static readonly int DecayId = Shader.PropertyToID("_Decay");
        static readonly int DiffusionId = Shader.PropertyToID("_Diffusion");
        static readonly int MapMinId = Shader.PropertyToID("_MapMin");
        static readonly int FoamDriftId = Shader.PropertyToID("_FoamDrift");
        static readonly int WhitecapAmountId = Shader.PropertyToID("_WhitecapAmount");
        static readonly int WhitecapBiasId = Shader.PropertyToID("_WhitecapBias");
        static readonly int DriftUvId = Shader.PropertyToID("_DriftUV");
        static readonly int MapSizeId = Shader.PropertyToID("_MapSize");

        // Listen to the detector while enabled. Unsubscribing matters: otherwise the detector
        // would keep calling a foam system that has been disabled or destroyed.
        void OnEnable()
        {
            Allocate();
            if (detector != null) detector.WaveBreak += OnWaveBreak;
        }

        void OnDisable()
        {
            if (detector != null) detector.WaveBreak -= OnWaveBreak;
            Release();
            // Tell shaders "no foam map" (all-zero rect = every uv outside). Without this, the water
            // kept sampling the freed map after Play mode and showed a white rectangle in Edit mode.
            Shader.SetGlobalVector(FoamRectId, Vector4.zero);
        }

        // ---------------------------------------------------------------
        // Textures
        // ---------------------------------------------------------------

        /// <summary>Texture size in texels: 'resolution' on the long side, square texels.</summary>
        public Vector2Int Texels
        {
            get
            {
                float longest = Mathf.Max(worldSize.x, worldSize.y);
                return new Vector2Int(Mathf.Max(8, Mathf.RoundToInt(resolution * worldSize.x / longest)),
                                      Mathf.Max(8, Mathf.RoundToInt(resolution * worldSize.y / longest)));
            }
        }

        void Allocate()
        {
            Release();
            if (updateShader == null) { Debug.LogError(Tag + "No update shader assigned.", this); return; }
            material = new Material(updateShader) { hideFlags = HideFlags.HideAndDontSave };

            // 32-bit float: half precision would round exp(-dt/tau) (~0.996 per step) and bias tau.
            current = CreateMap();
            previous = CreateMap();
            Clear();
            Publish();
            if (debugLogs)
                Debug.Log(Tag + $"Foam map {Texels.x}x{Texels.y} texels over {worldSize.x}x{worldSize.y} m " +
                          $"({worldSize.x / Texels.x:F3} m per texel), tau = {decayTime} s", this);
        }

        RenderTexture CreateMap()
        {
            Vector2Int size = Texels;
            var rt = new RenderTexture(size.x, size.y, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "FoamMap",
                wrapMode = TextureWrapMode.Clamp,     // no foam wrapping round from the other edge
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            rt.Create();
            return rt;
        }

        void Release()
        {
            if (current != null) { current.Release(); Destroy(current); }
            if (previous != null) { previous.Release(); Destroy(previous); }
            if (material != null) Destroy(material);
            current = previous = null;
            material = null;
        }

        /// <summary>Remove all foam and queued splats (called when the clock restarts).</summary>
        public void Clear()
        {
            pending.Clear();
            trace.Clear();
            simTime = 0f;
            generation++;
            TotalSplats = 0;
            DroppedSplats = 0;
            placementChecked = 0;          // S5.2: check the placement again after a restart
            placementWorstError = 0f;
            foreach (RenderTexture rt in new[] { current, previous })
            {
                if (rt == null) continue;
                RenderTexture active = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(false, true, Color.clear);
                RenderTexture.active = active;
            }
        }

        /// <summary>Make the map visible to every shader (the water shader uses it from S3.4).</summary>
        void Publish()
        {
            Shader.SetGlobalTexture(FoamTexId, current);
            // xy = world position of uv (0,0); zw = 1 / size, so uv = (world - xy) * zw.
            Shader.SetGlobalVector(FoamRectId, new Vector4(centre.x - worldSize.x * 0.5f, centre.y - worldSize.y * 0.5f,
                                                           1f / worldSize.x, 1f / worldSize.y));
            // S4.10f: the water's foam pattern moves with the same drift as the map.
            Shader.SetGlobalVector(FoamDriftId, new Vector4(drift.x, drift.y, 0f, 0f));
        }

        // ---------------------------------------------------------------
        // Splats and the simulation step
        // ---------------------------------------------------------------

        /// <summary>Map coordinates (metres, x and z) -> uv. S5.2: map coordinates are REST positions.</summary>
        public Vector2 WorldToUv(Vector2 mapXZ) =>
            new Vector2((mapXZ.x - centre.x) / worldSize.x + 0.5f, (mapXZ.y - centre.y) / worldSize.y + 0.5f);

        /// <summary>
        /// Queue a Gaussian blob of foam at a REST position (x0, z0): added at the next step.
        /// The test splat uses this directly, so its position means "on the particle resting there".
        /// </summary>
        public void AddSplat(Vector2 restXZ, float radius, float strength)
        {
            Vector2 uv = WorldToUv(restXZ);
            pending.Add(new Vector4(uv.x, uv.y, Mathf.Max(0.01f, radius), strength));
            TotalSplats++;
        }

        // S5.2 check: how well the splat lands on the impact point (first events only).
        const int PlacementChecks = 50;
        int placementChecked;
        float placementWorstError;

        /// <summary>
        /// S5.2: queue foam at a WORLD point. Finds the particle that is there now (the same fixed-point
        /// inversion as the detector, 8 iterations) and puts the foam on it.
        /// </summary>
        public void AddSplatAtWorldPoint(Vector2 worldXZ, float radius, float strength)
        {
            WaveField waves = detector != null ? detector.waves : null;
            if (waves == null) { AddSplat(worldXZ, radius, strength); return; }   // no waves: rest = world

            float t = waves.SimTime;
            Vector2 rest = waves.FindRestPosition(worldXZ.x, worldXZ.y, t, WaveField.DefaultInversionIterations);
            AddSplat(rest, radius, strength);

            // Check (decision rule 1 of S5.2): the particle we chose must really be at the impact point.
            if (placementChecked < PlacementChecks)
            {
                Vector3 drawn = waves.Evaluate(rest.x, rest.y, t).position;
                float error = Vector2.Distance(new Vector2(drawn.x, drawn.z), worldXZ);
                placementWorstError = Mathf.Max(placementWorstError, error);
                if (++placementChecked == PlacementChecks && debugLogs)
                    Debug.Log(Tag + $"Splat placement over the first {PlacementChecks} events: worst error " +
                              $"{placementWorstError * 1000f:F2} mm (rule: < 10 mm)", this);
            }
        }

        /// <summary>A wave broke on the rock: put foam on the water that hit.</summary>
        void OnWaveBreak(WaveBreakEvent e)
        {
            Vector3 p = e.position + e.outward * splatOutset;   // a little out from the rock face
            AddSplatAtWorldPoint(new Vector2(p.x, p.z),
                     splatRadius * (0.6f + 0.8f * e.intensity),   // weak hit 0.6x, full hit 1.4x
                     splatStrength * e.intensity);
        }

        [ContextMenu("Add Test Splat")]
        public void AddTestSplat()
        {
            AddSplat(testSplatPosition, testSplatRadius, testSplatStrength);
            TrackPoint(testSplatPosition);
        }

        /// <summary>Called by the clock every step, after the wave-break detector.</summary>
        public void Tick(float dt)
        {
            if (material == null) return;
            simTime += dt;

            // More splats than one pass can take: keep the strongest.
            if (pending.Count > MaxSplatsPerStep) pending.Sort((a, b) => b.w.CompareTo(a.w));
            SplatsLastStep = Mathf.Min(pending.Count, MaxSplatsPerStep);
            DroppedSplats += pending.Count - SplatsLastStep;
            for (int i = 0; i < MaxSplatsPerStep; i++)
                splatArray[i] = i < SplatsLastStep ? pending[i] : Vector4.zero;
            pending.Clear();

            // Diffusion weight for the whole frame: alpha = 4 D dt / h^2 (h = texel size in metres).
            // If it is too big for one stable pass, split the frame into n equal substeps.
            float h = worldSize.x / Texels.x;
            float alpha = 4f * diffusion * dt / (h * h);
            int n = Mathf.Clamp(Mathf.CeilToInt(alpha / MaxAlpha), 1, MaxSubsteps);
            SubstepsLastStep = n;

            material.SetVectorArray(SplatsId, splatArray);
            material.SetVector(MapSizeId, worldSize);
            material.SetFloat(DiffusionId, Mathf.Min(alpha / n, 1f));
            // S4.8: drift per pass (in uv) and whitecap settings.
            material.SetVector(MapMinId, new Vector2(centre.x - worldSize.x * 0.5f, centre.y - worldSize.y * 0.5f));
            material.SetVector(DriftUvId, new Vector2(drift.x * dt / n / worldSize.x, drift.y * dt / n / worldSize.y));
            material.SetFloat(WhitecapBiasId, whitecapBias);
            // Decay per substep: n substeps multiply to exactly exp(-dt / tau).
            material.SetFloat(DecayId, Mathf.Exp(-dt / n / Mathf.Max(0.01f, decayTime)));

            for (int k = 0; k < n; k++)
            {
                // New splats go in once, in the last substep.
                material.SetFloat(SplatCountId, k == n - 1 ? SplatsLastStep : 0);
                material.SetFloat(WhitecapAmountId, k == n - 1 ? whitecapRate * dt : 0f);   // once per frame
                (current, previous) = (previous, current);   // last result becomes the input
                Graphics.Blit(previous, current, material, 0);
            }
            Publish();

            if (recordTrace) RequestReadback();
        }

        // ---------------------------------------------------------------
        // Debug: read one texel back, save the trace, draw a preview
        // ---------------------------------------------------------------

        /// <summary>Choose the world point whose foam value is recorded every step.</summary>
        public void TrackPoint(Vector2 worldXZ)
        {
            Vector2 uv = WorldToUv(worldXZ);
            Vector2Int size = Texels;
            traceTexel = new Vector2Int(Mathf.Clamp((int)(uv.x * size.x), 0, size.x - 1),
                                        Mathf.Clamp((int)(uv.y * size.y), 0, size.y - 1));
        }

        void RequestReadback()
        {
            if (traceTexel.x < 0 || !SystemInfo.supportsAsyncGPUReadback) return;
            float t = simTime;   // the value belongs to THIS step, even though it arrives a few frames later
            int gen = generation;
            AsyncGPUReadback.Request(current, 0, traceTexel.x, 1, traceTexel.y, 1, 0, 1, TextureFormat.RFloat, request =>
            {
                // Arrived after a restart? Then it belongs to the old run: drop it.
                if (!request.hasError && gen == generation) trace.Add(new Vector2(t, request.GetData<float>()[0]));
            });
        }

        /// <summary>
        /// Debug/measurement: the whole map's foam, integrated over area (sum of F x texel area, in m^2).
        /// Diffusion only moves foam around, so without new splats this falls exactly as e^(-t/tau).
        /// Reads the whole texture back from the GPU, which stalls the frame, so it is never called every frame.
        /// </summary>
        public float TotalFoam()
        {
            if (current == null) return 0f;
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = current;
            var copy = new Texture2D(current.width, current.height, TextureFormat.RFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, current.width, current.height), 0, 0);
            copy.Apply();
            RenderTexture.active = active;

            double sum = 0;
            foreach (float v in copy.GetPixelData<float>(0)) sum += v;
            Destroy(copy);
            float texelArea = (worldSize.x / Texels.x) * (worldSize.y / Texels.y);
            return (float)(sum * texelArea);
        }

        /// <summary>Simulation time as the foam system counts it (since the last Clear()).</summary>
        public float SimTime => simTime;

        /// <summary>Write the recorded (time, foam) pairs as CSV. Returns the number of rows.</summary>
        public int SaveTraceCsv(string path)
        {
            trace.Sort((a, b) => a.x.CompareTo(b.x));
            var csv = new StringBuilder("time_s,foam\n");
            foreach (Vector2 p in trace)
                csv.Append(p.x.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                   .Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, csv.ToString());
            return trace.Count;
        }

        void OnGUI()
        {
            if (!showPreview || current == null) return;
            // Keep the map's shape (100 x 260 m -> tall and narrow); u = world X to the right, v = world Z up.
            float h = Mathf.Min(320f, Screen.height * 0.45f);
            float w = h * worldSize.x / worldSize.y;
            var rect = new Rect(Screen.width - w - 10f, Screen.height - h - 10f, w, h);
            GUI.DrawTexture(rect, Texture2D.blackTexture);
            GUI.DrawTexture(rect, current, ScaleMode.StretchToFill, false);
            GUI.Label(new Rect(rect.x, rect.y - 20f, w, 20f), "foam map");
        }
    }
}
