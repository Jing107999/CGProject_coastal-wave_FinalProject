using UnityEngine;
using UnityEngine.Rendering;

namespace Coastal
{
    /// <summary>
    /// The sea floor (S4.10): a procedural grid that is deep out at sea and rises to a shelf at the
    /// foot of the cliff.
    ///
    /// DEEP ON PURPOSE. Our waves use the deep-water dispersion w = sqrt(g k), which holds where the
    /// depth h is more than half a wavelength. The seabed is chosen to keep that (nearly) true:
    ///   - open sea at 35 m: h > lambda / 2 for every wave, including the 70 m one;
    ///   - shelf at the cliff foot ~16.5 m: the 70 m wave's speed would be ~5 % lower in reality
    ///     (1 - sqrt(tanh(k h))), the 48 m wave ~1 %, all shorter waves ~0 %.
    /// The foot is also far deeper than the ~7 m at which our tallest waves (H up to ~5.8 m) would
    /// start to break on the bottom (H/h ~ 0.78), so "waves reflect off a cliff standing in deep
    /// water instead of breaking on a beach" stays consistent.
    ///
    /// Height at a point (x, z):
    ///   d    = distance out to sea from the cliff face at the shelf's depth (0 at / under the rock)
    ///   h    = deepLevel + (footLevel - deepLevel) * exp(-d / shelfWidth)   (rises towards the cliff)
    ///        + detail * noise(x, z)                                          (gentle undulations)
    /// The distance is measured from the face at depth, not from the waterline, so the shelf meets
    /// the rock even where the cliff's base is cut back or juts out; it buries the cliff's bottom edge.
    /// Colour: sand out at sea, darker pebbles and rubble near the cliff (vertex colours, read by
    /// the rock shader; the alpha channel is the rock shader's ambient occlusion, 1 = open).
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SeabedGenerator : MonoBehaviour
    {
        public CliffGenerator cliff;

        [Header("Grid (centred on this object)")]
        public float size = 400f;
        [Range(16, 512)] public int resolution = 200;   // 2 m cells; the floor is smooth and far below

        [Header("Depth")]
        [Tooltip("Open-sea floor (m). -35 m = deep water for every wave in our sea (h > lambda/2 for 70 m).")]
        public float deepLevel = -35f;
        [Tooltip("Floor at the cliff foot (m). Must stay below the cliff's bottom edge minus the detail, " +
                 "and deep enough for the deep-water waves (see the class comment).")]
        public float footLevel = -16.5f;
        [Tooltip("How far out to sea the shelf reaches (e-folding distance, m).")]
        public float shelfWidth = 35f;
        [Tooltip("Undulation height (m, +/-).")]
        public float detail = 1f;
        [Tooltip("Undulation frequency (per metre).")]
        public float detailFrequency = 0.05f;

        [Header("Colour")]
        public Color sandColor = new Color(0.62f, 0.56f, 0.43f);
        public Color pebbleColor = new Color(0.33f, 0.31f, 0.28f);
        [Tooltip("Distance from the cliff over which rubble gives way to sand (m).")]
        public float rubbleWidth = 12f;

        public bool debugLogs = true;
        const string Tag = "[Seabed] ";

        // Perlin/FBM values rarely leave about +/-0.4; this stretches them to about +/-1 (as in the cliff).
        const float NoiseToUnit = 2.5f;

        Mesh mesh;

        // Start, not OnEnable: the cliff builds its structure in ITS OnEnable, and Start runs after all of them.
        void Start() => Build();

        void OnDestroy()
        {
            if (mesh == null) return;
            if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
        }

        /// <summary>Floor height (m) at world (x, z), given the cliff face's x at the shelf depth for this z.</summary>
        public float HeightAt(float x, float z, float faceX)
        {
            float d = Mathf.Max(faceX - x, 0f);             // the sea is on the -x side of the rock
            float h = deepLevel + (footLevel - deepLevel) * Mathf.Exp(-d / shelfWidth);
            float n = Mathf.Clamp(NoiseToUnit * Noise3D.Fbm(new Vector3(x * detailFrequency, 7.3f, z * detailFrequency), 4), -1f, 1f);
            return h + detail * n;
        }

        /// <summary>
        /// Where the shelf starts for coast position z: the face's x at the shelf depth OR at the
        /// cliff's bottom edge, whichever sticks out further to sea (smaller x). In places the base
        /// juts out up to ~3.7 m beyond the face at the shelf depth; measuring only at the shelf
        /// depth left the bottom edge exposed there (found by the burial check, S4.10 run 1).
        /// </summary>
        public float FaceX(float z) => Mathf.Min(cliff.WallX(z, footLevel), cliff.WallX(z, cliff.bottom));

        [ContextMenu("Build")]
        public void Build()
        {
            if (cliff == null) { Debug.LogError(Tag + "No cliff assigned.", this); return; }
            var timer = System.Diagnostics.Stopwatch.StartNew();

            int n = resolution + 1;
            var vertices = new Vector3[n * n];
            var colours = new Color[n * n];
            float half = size * 0.5f;
            Vector3 origin = transform.position;

            for (int j = 0; j < n; j++)
            {
                float z = origin.z - half + size * j / resolution;
                // The face position only depends on z: look it up once per row.
                float faceX = FaceX(z);
                for (int i = 0; i < n; i++)
                {
                    float x = origin.x - half + size * i / resolution;
                    float h = HeightAt(x, z, faceX);
                    vertices[j * n + i] = new Vector3(x - origin.x, h - origin.y, z - origin.z);

                    // Rubble near the rock, sand further out, with a little patchiness from the same noise.
                    float rubble = Mathf.Exp(-Mathf.Max(faceX - x, 0f) / rubbleWidth);
                    float patch = 0.5f + 0.5f * Mathf.Clamp(NoiseToUnit * Noise3D.Fbm(new Vector3(x * 0.15f, 2.1f, z * 0.15f), 2), -1f, 1f);
                    Color c = Color.Lerp(sandColor, pebbleColor, Mathf.Clamp01(rubble + 0.3f * patch));
                    c.a = 1f;                                 // AO channel for the rock shader: fully open
                    colours[j * n + i] = c;
                }
            }

            // Two triangles per cell, wound so the normals point up.
            var triangles = new int[resolution * resolution * 6];
            int t = 0;
            for (int j = 0; j < resolution; j++)
                for (int i = 0; i < resolution; i++)
                {
                    int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b;
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d;
                }

            if (mesh == null)
                mesh = new Mesh { name = "Seabed", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            mesh.Clear();
            mesh.vertices = vertices;
            mesh.colors = colours;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = mesh;

            if (debugLogs)
            {
                // Check: the floor must cover the cliff's bottom edge everywhere along the coast.
                float minCover = float.MaxValue, worstZ = 0f;
                for (int k = 0; k <= 400; k++)
                {
                    float z = cliff.zMin + (cliff.zMax - cliff.zMin) * k / 400f;
                    float baseX = cliff.WallX(z, cliff.bottom);
                    float cover = HeightAt(baseX, z, FaceX(z)) - cliff.bottom;
                    if (cover < minCover) { minCover = cover; worstZ = z; }
                }
                Debug.Log(Tag + $"{n}x{n} vertices over {size} m, floor {deepLevel} m at sea to {footLevel} m at the cliff " +
                          $"(+/-{detail} m), built in {timer.ElapsedMilliseconds} ms. Cliff bottom edge buried by at least " +
                          $"{minCover:0.00} m (worst at z = {worstZ:0} m){(minCover < 0f ? "  <-- EXPOSED" : "")}", this);
            }
        }
    }
}
