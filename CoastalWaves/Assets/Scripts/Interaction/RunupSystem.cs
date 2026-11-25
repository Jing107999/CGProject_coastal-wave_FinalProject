using System.Collections.Generic;
using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// White water running up the cliff face and draining down again (S4.4).
    ///
    /// The map lies ON the cliff face: u = coast position z, v = height y. Each wave-break event
    /// launches an UPRUSH: a jet of water climbing the rock like a thrown ball,
    ///     y(t) = y0 + v0 t - g t^2 / 2,   v0 = sqrt(2 g R),
    /// which reaches the run-up height R above the crest after t = v0 / g, then stops. While it
    /// climbs, each step deposits white water on the part of the face it passed. The map then
    /// fades (exp(-dt/tau)) and slides down (drain), in RunupUpdate.shader.
    /// R grows with the event's intensity, so stronger hits climb higher and wider.
    ///
    /// The rock shader reads the map (_RunupTex, _RunupRect) and whitens the stone there.
    /// </summary>
    public class RunupSystem : MonoBehaviour
    {
        const int MaxBands = 64;   // must match MAX_BANDS in RunupUpdate.shader

        public WaveBreakDetector detector;
        [Tooltip("Hidden/Coastal/RunupUpdate")]
        public Shader updateShader;

        [Header("Map (cliff face: z along the coast, y up)")]
        public float zMin = -200f, zMax = 200f;       // S4.10b: the whole coast (was -130..130)
        public float yMin = -4f, yMax = 26f;
        public int resolutionZ = 1576, resolutionY = 128;   // keeps ~0.25 m along the coast

        [Header("Uprush")]
        [Tooltip("Run-up above the crest for the weakest hit (m).")]
        public float runupBase = 2.5f;
        [Tooltip("Extra run-up per unit of intensity (m): R = base + scale * intensity.")]
        public float runupScale = 16f;
        public float gravity = 9.81f;
        [Tooltip("Half-width of the jet across the coast (m): width + perIntensity * intensity.")]
        public float bandWidth = 3f, bandWidthPerIntensity = 4f;
        [Tooltip("White water left by a full-intensity jet (half of it for the weakest).")]
        public float strength = 0.9f;

        [Header("Afterwards")]
        [Tooltip("Lifetime of the white water on the rock (s): down to 37 % after this.")]
        public float decayTime = 2.2f;
        [Tooltip("How fast the white water slides down the face (m/s).")]
        public float drainSpeed = 2.5f;
        [Tooltip("S4.6: how long the stone stays wet after the water is gone (s): down to 37 % after this.")]
        public float wetDecayTime = 20f;

        [Header("Debug")]
        public bool debugLogs = true;
        const string Tag = "[Runup] ";

        public RenderTexture RunupTexture => current;
        /// <summary>Uprushes launched since the last Clear().</summary>
        public int TotalUprushes { get; private set; }
        /// <summary>Uprushes still climbing.</summary>
        public int ActiveUprushes => active.Count;
        /// <summary>Highest point any jet reached since the last Clear() (m).</summary>
        public float HighestFront { get; private set; }

        struct Uprush { public float z, y0, v0, halfWidth, strength, age, lastY; }

        readonly List<Uprush> active = new List<Uprush>();
        readonly Vector4[] bands = new Vector4[MaxBands];
        readonly float[] bandHalfWidth = new float[MaxBands];
        RenderTexture current, previous;
        Material material;

        static readonly int RunupTexId = Shader.PropertyToID("_RunupTex");
        static readonly int RunupRectId = Shader.PropertyToID("_RunupRect");
        static readonly int BandsId = Shader.PropertyToID("_Bands");
        static readonly int HalfWidthId = Shader.PropertyToID("_BandHalfWidth");
        static readonly int CountId = Shader.PropertyToID("_BandCount");
        static readonly int DecayId = Shader.PropertyToID("_Decay");
        static readonly int WetDecayId = Shader.PropertyToID("_WetDecay");
        static readonly int DrainId = Shader.PropertyToID("_Drain");
        static readonly int MapRectId = Shader.PropertyToID("_MapRect");

        void OnEnable()
        {
            Allocate();
            if (detector != null) detector.WaveBreak += OnWaveBreak;
        }

        void OnDisable()
        {
            if (detector != null) detector.WaveBreak -= OnWaveBreak;
            Release();
        }

        void Allocate()
        {
            Release();
            if (updateShader == null) { Debug.LogError(Tag + "No update shader assigned.", this); return; }
            material = new Material(updateShader) { hideFlags = HideFlags.HideAndDontSave };
            current = CreateMap();
            previous = CreateMap();
            Clear();
            if (debugLogs)
                Debug.Log(Tag + $"Run-up map {resolutionZ}x{resolutionY} over z {zMin}..{zMax} m, y {yMin}..{yMax} m", this);
        }

        RenderTexture CreateMap()
        {
            // RG: R = water film, G = wetness (S4.6).
            var rt = new RenderTexture(resolutionZ, resolutionY, 0, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear)
            {
                name = "RunupMap",
                wrapMode = TextureWrapMode.Clamp,
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

        /// <summary>Remove all white water and running jets (called when the clock restarts).</summary>
        public void Clear()
        {
            active.Clear();
            TotalUprushes = 0;
            HighestFront = 0f;
            foreach (RenderTexture rt in new[] { current, previous })
            {
                if (rt == null) continue;
                RenderTexture was = RenderTexture.active;
                RenderTexture.active = rt;
                GL.Clear(false, true, Color.clear);
                RenderTexture.active = was;
            }
            Publish();
        }

        void Publish()
        {
            Shader.SetGlobalTexture(RunupTexId, current != null ? (Texture)current : Texture2D.blackTexture);
            Shader.SetGlobalVector(RunupRectId, new Vector4(zMin, yMin, 1f / (zMax - zMin), 1f / (yMax - yMin)));
        }

        /// <summary>Run-up height above the crest for an event of this intensity (m).</summary>
        public float RunUp(float intensity) => runupBase + runupScale * intensity;

        /// <summary>A wave broke on the rock: launch a jet up the face.</summary>
        void OnWaveBreak(WaveBreakEvent e) => Launch(e.position.z, e.position.y, e.intensity);

        /// <summary>Start an uprush at coast position z from height y0 (public so a test can call it).</summary>
        public void Launch(float z, float y0, float intensity)
        {
            float r = RunUp(intensity);
            active.Add(new Uprush
            {
                z = z,
                y0 = y0,
                v0 = Mathf.Sqrt(2f * gravity * r),
                halfWidth = bandWidth + bandWidthPerIntensity * intensity,
                strength = strength * Mathf.Lerp(0.5f, 1f, intensity),
                age = 0f,
                lastY = y0,
            });
            TotalUprushes++;
            if (active.Count > MaxBands) active.RemoveAt(0);   // never more than one pass can draw
        }

        /// <summary>Called by the clock every step, after the detector (so this step's hits launch now).</summary>
        public void Tick(float dt)
        {
            if (material == null) return;

            // Move every jet along its ballistic path; queue the stretch it climbed this step.
            int n = 0;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                Uprush u = active[i];
                u.age += dt;
                float tApex = u.v0 / gravity;
                float t = Mathf.Min(u.age, tApex);
                float y = u.y0 + u.v0 * t - 0.5f * gravity * t * t;
                HighestFront = Mathf.Max(HighestFront, y);

                bands[n] = new Vector4(u.z, u.lastY, y, u.strength);
                bandHalfWidth[n] = u.halfWidth;
                n++;

                u.lastY = y;
                if (u.age >= tApex) active.RemoveAt(i);   // reached the top: no more climbing
                else active[i] = u;
            }
            for (int i = n; i < MaxBands; i++) { bands[i] = Vector4.zero; bandHalfWidth[i] = 1f; }

            material.SetVectorArray(BandsId, bands);
            material.SetFloatArray(HalfWidthId, bandHalfWidth);
            material.SetFloat(CountId, n);
            material.SetFloat(DecayId, Mathf.Exp(-dt / Mathf.Max(0.01f, decayTime)));
            material.SetFloat(WetDecayId, Mathf.Exp(-dt / Mathf.Max(0.01f, wetDecayTime)));
            material.SetFloat(DrainId, drainSpeed * dt / (yMax - yMin));
            material.SetVector(MapRectId, new Vector4(zMin, yMin, zMax - zMin, yMax - yMin));

            (current, previous) = (previous, current);
            Graphics.Blit(previous, current, material, 0);
            Publish();
        }

        /// <summary>
        /// Debug: read the whole map back. Returns (max water film, area with film > 0.1 in m^2,
        /// max wetness, area with wetness > 0.1 in m^2).
        /// </summary>
        public Vector4 MapStats()
        {
            if (current == null) return Vector2.zero;
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = current;
            var copy = new Texture2D(current.width, current.height, TextureFormat.RGFloat, false, true);
            copy.ReadPixels(new Rect(0, 0, current.width, current.height), 0, 0);
            copy.Apply();
            RenderTexture.active = was;
            float maxFilm = 0f, maxWet = 0f; int film = 0, wet = 0;
            foreach (Vector2 v in copy.GetPixelData<Vector2>(0))
            {
                maxFilm = Mathf.Max(maxFilm, v.x); if (v.x > 0.1f) film++;
                maxWet = Mathf.Max(maxWet, v.y); if (v.y > 0.1f) wet++;
            }
            Destroy(copy);
            float texelArea = (zMax - zMin) / resolutionZ * (yMax - yMin) / resolutionY;
            return new Vector4(maxFilm, film * texelArea, maxWet, wet * texelArea);
        }
    }
}
