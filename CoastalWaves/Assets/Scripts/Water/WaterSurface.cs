using UnityEngine;
using UnityEngine.Rendering;

namespace Coastal
{
    /// <summary>
    /// Builds the flat grid of triangles that the water shader will later bend into
    /// waves.
    ///
    /// WHY: waves can only appear where there are vertices. Unity's built-in Plane
    /// has 10 x 10 squares, far too few for 2 m waves, so we generate our own grid.
    ///
    /// WHAT: a square (N+1) x (N+1) vertex grid lying in the XZ plane at height 0,
    /// with more vertices near the centre (where the rock stands) and fewer at the
    /// edges (far away, hidden by fog).
    ///
    /// HOW: Build() fills the vertex, UV and triangle arrays, then hands them to a
    /// Mesh. The mesh is generated whenever the object is enabled or a setting
    /// changes, in Edit mode too, and is never saved into the scene file.
    /// </summary>
    [ExecuteAlways] // run in Edit mode as well, so the grid is visible without pressing Play
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class WaterSurface : MonoBehaviour
    {
        // ---------------------------------------------------------------
        // Inspector settings
        // ---------------------------------------------------------------

        [Tooltip("Width and length of the square water sheet, in metres.")]
        [Min(10f)] public float size = 400f;

        [Tooltip("Number of squares along each side. Vertices = (resolution + 1)^2.")]
        [Range(16, 1024)] public int resolution = 512;

        [Tooltip("0 = evenly spaced vertices. Higher values pull vertices towards the " +
                 "centre: spacing there becomes (1 - bias) times the uniform spacing.")]
        [Range(0f, 0.9f)] public float centreBias = 0.75f;

        [Tooltip("Highest a wave can rise or sink (metres). Used to enlarge the mesh " +
                 "bounds so Unity doesn't cull the water when the shader lifts it.")]
        [Min(0f)] public float maxWaveHeight = 8f;

        [Header("Debug")]
        [Tooltip("Write a line to the Console every time the grid is (re)built.")]
        public bool debugLogs = true;

        // Tag at the start of every Console message from this script (filter on "[Water]").
        const string Tag = "[Water] ";

        // ---------------------------------------------------------------
        // Read-only info
        // ---------------------------------------------------------------

        public int VertexCount => (resolution + 1) * (resolution + 1);
        public int TriangleCount => resolution * resolution * 2;

        /// <summary>Distance between neighbouring vertices at the very centre (metres).</summary>
        public float CentreSpacing => size / resolution * (1f - centreBias);

        /// <summary>Distance between neighbouring vertices at the outer edge (metres).</summary>
        public float EdgeSpacing => size / resolution * (1f + 2f * centreBias);

        // The settings the current mesh was built with, so we only rebuild when
        // something actually changed.
        int builtResolution = -1;
        float builtSize, builtBias, builtHeight;

        // ---------------------------------------------------------------
        // Unity callbacks
        // ---------------------------------------------------------------

        void OnEnable() => Build();

        // Called in the Editor whenever a value is changed in the Inspector.
        void OnValidate()
        {
            // Building a mesh inside OnValidate is not allowed in some editor states,
            // so we schedule it for right after the Inspector change is processed.
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this != null && isActiveAndEnabled) Build();
            };
#endif
        }

        // ---------------------------------------------------------------
        // Mesh generation
        // ---------------------------------------------------------------

        /// <summary>Generate (or regenerate) the grid mesh. Does nothing if nothing changed.</summary>
        [ContextMenu("Rebuild Mesh")]
        public void Build()
        {
            var filter = GetComponent<MeshFilter>();
            bool unchanged = filter.sharedMesh != null &&
                             builtResolution == resolution &&
                             Mathf.Approximately(builtSize, size) &&
                             Mathf.Approximately(builtBias, centreBias) &&
                             Mathf.Approximately(builtHeight, maxWaveHeight);
            if (unchanged) return;

            // Debug: time how long generation takes.
            var timer = System.Diagnostics.Stopwatch.StartNew();

            int n = resolution + 1;                    // vertices per side
            var vertices = new Vector3[n * n];
            var uvs = new Vector2[n * n];

            // 1. VERTICES: walk the grid row by row (j = row along Z, i = column along X).
            for (int j = 0; j < n; j++)
            {
                float v = Warp(j / (float)resolution);   // 0..1, squeezed towards the centre
                for (int i = 0; i < n; i++)
                {
                    float u = Warp(i / (float)resolution);
                    int index = j * n + i;               // position of vertex (i, j) in the flat array

                    // Map 0..1 to -size/2..+size/2, so the grid is centred on the object.
                    vertices[index] = new Vector3((u - 0.5f) * size, 0f, (v - 0.5f) * size);

                    // UV = where this vertex sits on the sheet (0..1). Used later for textures.
                    uvs[index] = new Vector2(u, v);
                }
            }

            // 2. TRIANGLES: two per grid square, 3 indices each.
            //
            //   c ─── d      a = this vertex, b = right neighbour,
            //   │ ╲   │      c = vertex above, d = above-right.
            //   │  ╲  │      Listing them clockwise (seen from above)
            //   a ─── b      makes the top side the front face.
            var triangles = new int[resolution * resolution * 6];
            int t = 0;
            for (int j = 0; j < resolution; j++)
            {
                for (int i = 0; i < resolution; i++)
                {
                    int a = j * n + i;
                    int b = a + 1;
                    int c = a + n;
                    int d = c + 1;

                    triangles[t++] = a; triangles[t++] = c; triangles[t++] = b; // lower-left triangle
                    triangles[t++] = b; triangles[t++] = c; triangles[t++] = d; // upper-right triangle
                }
            }

            // 3. MESH: hand the arrays to Unity.
            var mesh = new Mesh
            {
                name = "WaterGrid",
                // 16-bit indices stop at 65,535 vertices; we have far more, so use 32-bit.
                indexFormat = IndexFormat.UInt32,
                // Never write this generated mesh into the scene file (it's rebuilt on load).
                hideFlags = HideFlags.DontSave,
            };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals(); // all point straight up for now; the shader computes real ones

            // 4. BOUNDS: the grid is flat, but the shader will move vertices up/down by up
            // to maxWaveHeight and sideways a little. Grow the box so it still contains the
            // waves; otherwise Unity may think the water is off-screen and skip drawing it.
            float h = maxWaveHeight;
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(size + 2f * h, 2f * h, size + 2f * h));

            // Replace the previous mesh (and free its memory).
            if (filter.sharedMesh != null && filter.sharedMesh.name == "WaterGrid")
                DestroyImmediate(filter.sharedMesh);
            filter.sharedMesh = mesh;

            builtResolution = resolution;
            builtSize = size;
            builtBias = centreBias;
            builtHeight = maxWaveHeight;

            timer.Stop();
            if (debugLogs)
            {
                Debug.Log(Tag + $"Built grid {resolution} x {resolution} over {size:F0} m: " +
                          $"{VertexCount:N0} vertices, {TriangleCount:N0} triangles, " +
                          $"spacing {CentreSpacing:F2} m (centre) to {EdgeSpacing:F2} m (edge), " +
                          $"bounds height ±{maxWaveHeight:F1} m, took {timer.Elapsed.TotalMilliseconds:F1} ms", this);
            }
        }

        /// <summary>
        /// Squeezes a 0..1 grid coordinate towards the middle.
        /// c' = c * ((1 - b) + b * c^2), with c in -1..1.
        /// The slope is (1 - b) at the centre (dense) and (1 + 2b) at the edges (sparse).
        /// </summary>
        float Warp(float x)
        {
            float c = x * 2f - 1f;                                    // 0..1  ->  -1..1
            float warped = c * ((1f - centreBias) + centreBias * c * c);
            return warped * 0.5f + 0.5f;                              // -1..1 ->  0..1
        }
    }
}
