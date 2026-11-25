using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace Coastal
{
    /// <summary>
    /// A procedural sea cliff running along the Z axis and facing the open sea (-X).
    /// The waves travel along +X, so they run straight into it.
    ///
    /// LOOK (D27): dark slate in one colour with varied hues, rust staining as the only extra
    /// tint, a wet olive zone at the water, thin bedding, vertical joints splitting the face
    /// into pillars and blocks, deep clefts, a few sea caves, and cracks. Crevices are dark
    /// because of LIGHT: the sun's shadows plus baked ambient occlusion.
    ///
    /// HOW: the face is a "sideways height field": WallX(z, y) = how far the rock sticks out
    /// to sea. A grid of samples becomes the mesh; a plateau closes the top. Vertex colour
    /// RGB = rock colour (linear), alpha = ambient occlusion.
    ///
    /// PERFORMANCE (P1): everything that depends only on z is computed once per column
    /// (ColumnData); rows are filled in parallel (all shape functions are pure); columns are
    /// concentrated near the camera; the collider only gets a thin band around the waterline.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class CliffGenerator : MonoBehaviour
    {
        // ---------------------------------------------------------------
        // Settings
        // ---------------------------------------------------------------

        [Header("Extent (metres)")]
        public float zMin = -200f, zMax = 200f;
        [Tooltip("Average x position of the cliff face. The sea is on the -X side.")]
        public float baseX = 25f;
        public float bottom = -18f, top = 45f;
        [Tooltip("How far the flat top extends inland (hides the back of the face).")]
        public float plateauDepth = 160f;

        [Header("Mesh resolution")]
        [Tooltip("Columns along the coast.")]
        [Range(50, 1600)] public int segmentsZ = 800;
        [Tooltip("Rows up the face (~0.18 m each with 360, needed for thin bedding).")]
        [Range(20, 600)] public int segmentsY = 360;
        [Range(2, 120)] public int segmentsPlateau = 40;
        [Tooltip("0 = evenly spaced columns. Higher = more columns near z = 0 (where the camera " +
                 "looks), fewer far away. Spacing at the centre = (1 - bias) x uniform.")]
        [Range(0f, 0.9f)] public float columnFocus = 0.6f;

        [Header("Collider (only the waterline band is needed for the probes)")]
        public float colliderBandMin = -4f, colliderBandMax = 10f;

        [Header("Irregular skyline")]
        [Tooltip("Large, slow variation of the cliff-top height along the coast (metres).")]
        public float skylineAmplitude = 7f;
        // S5.8: the sharper notches along the top edge (skylineRoughness, ridged noise +-2.5 m every
        // ~17 m) were removed. The ground dipped into each notch and hung over the lip like a skirt.

        [Header("Cliff-top ground (S5.6)")]
        [Tooltip("Distance inland (metres) over which the broken edge blends into smooth ground. " +
                 "The edge's notches, pillars and clefts fade out within this distance.")]
        public float topBlend = 8f;
        [Tooltip("Gentle roll of the ground inland (metres up and down).")]
        public float groundRoll = 1f;
        [Tooltip("Size of those rolls (metres between rises).")]
        public float groundRollScale = 60f;

        [Header("Bedding (thin horizontal layers)")]
        public Vector2 layerThickness = new Vector2(0.25f, 0.9f);
        [Tooltip("Tilt of the layers along the coast (metres of rise per metre of z).")]
        public float dip = 0.035f;
        public float thinLayerStep = 0.25f;
        public int layersPerBed = 6;
        public float bedRelief = 1.2f;

        [Header("Deep clefts (full-height vertical joints) and caves")]
        public Vector2 jointSpacing = new Vector2(2f, 6f);
        [Range(0f, 1f)] public float cleftChance = 0.08f;
        public Vector2 cleftDepth = new Vector2(1.5f, 3f);
        public Vector2 cleftWidth = new Vector2(0.8f, 1.6f);
        public float jointDepth = 0.9f;

        [Header("Outline: headlands and scalloped bays (round 4)")]
        [Tooltip("Number of rounded headlands poking out to sea.")]
        [Range(1, 8)] public int headlandCount = 5;
        [Tooltip("Headlands are spread evenly (with jitter) over this z range.")]
        public Vector2 headlandRegion = new Vector2(-150f, 150f);
        [Tooltip("How far each headland pokes out to sea (min, max metres).")]
        public Vector2 headlandProtrusion = new Vector2(12f, 20f);
        [Tooltip("Half-width of each headland along the coast (min, max metres).")]
        public Vector2 headlandHalfWidth = new Vector2(12f, 22f);
        [Tooltip("Shape of a headland seen from above: 2 = semicircle, larger = blunter.")]
        [Range(1.5f, 5f)] public float headlandShape = 3f;
        [Tooltip("How far the bays between headlands are carved back, as circular arcs (metres).")]
        public float bayDepth = 10f;
        [Tooltip("Rounding where a headland meets a bay (metres): removes sharp corners.")]
        public float junctionRounding = 6f;
        [Tooltip("One gentle large-scale bend of the whole coast (peak metres, ~150 m wavelength).")]
        public float coastBend = 4f;
        [Tooltip("Small, smooth irregularity so the arcs don't look drawn with a compass (metres).")]
        public float coastWobble = 0.5f;

        [Header("Profile: protruding cap, undercut base (P2)")]
        [Tooltip("How far the top ~10 m juts out to sea (metres), more where the cliff protrudes.")]
        public float capOverhang = 2.5f;
        [Tooltip("Depth of the wave-cut undercut around the waterline (metres), deeper where the cliff protrudes.")]
        public float undercutDepth = 3f;
        [Tooltip("Height of the undercut's centre above sea level, and its vertical size (metres).")]
        public float undercutCentre = 1.8f, undercutHeight = 3.2f;

        [Header("Stone cells (P3): warped Voronoi blocks and chips")]
        [Tooltip("Typical size of the large stone blocks (along the coast, up the face), metres.")]
        public Vector2 blockSize = new Vector2(6f, 4.5f);
        [Tooltip("How far each block sits forward/back (metres).")]
        public float blockRelief = 1.0f;
        [Tooltip("Random tilt of each block's face (metres per metre): gives angular facets.")]
        public float blockTilt = 0.22f;
        [Tooltip("Depth and half-width of the cracks along block borders (metres).")]
        public float blockCrackDepth = 0.9f, blockCrackWidth = 0.35f;
        [Tooltip("Size of the small chips inside blocks (metres).")]
        public Vector2 chipSize = new Vector2(1.4f, 1.0f);
        public float chipRelief = 0.35f, chipTilt = 0.15f;
        public float chipCrackDepth = 0.25f, chipCrackWidth = 0.2f;
        [Tooltip("Domain warping: how far (metres) the cell pattern is pushed around by noise, so borders aren't straight.")]
        public float warpAmount = 1.6f;
        public float warpScale = 0.06f;

        [Header("Sea caves")]
        [Range(0, 10)] public int caveCount = 4;
        public Vector2 caveRegion = new Vector2(-40f, 60f);
        public Vector2 caveDepth = new Vector2(5f, 8f);
        public Vector2 caveHalfWidth = new Vector2(2.5f, 4f);
        public Vector2 caveHeight = new Vector2(5f, 8f);

        [Header("Small-scale detail")]
        public float roughness = 0.3f;
        public int seed = 5;

        [Header("Colour (one base colour + one tint, D27); picked in sRGB")]
        public Color slateColor = new Color(0.30f, 0.28f, 0.26f);
        public Color warmShift = new Color(0.36f, 0.30f, 0.24f);
        public Color coolShift = new Color(0.25f, 0.26f, 0.29f);
        public Color rustColor = new Color(0.42f, 0.26f, 0.13f);
        [Range(0f, 1f)] public float rustAmount = 0.4f;
        public Color wetColor = new Color(0.20f, 0.22f, 0.11f);
        [Tooltip("Moss/algae green on the rock in the splash zone (above the water, where waves hit).")]
        public Color mossColor = new Color(0.25f, 0.30f, 0.12f);
        [Tooltip("Height (metres above sea level) up to which waves splash and moss grows; higher on protruding parts.")]
        public float splashHeight = 6.5f;
        public Color plateauColor = new Color(0.22f, 0.25f, 0.14f);

        [Header("Baked ambient occlusion (D27)")]
        public float aoFineRadius = 1.2f;
        public float aoBroadRadius = 5f;
        public float aoFineDepth = 0.5f;
        public float aoBroadDepth = 2.5f;
        [Range(0f, 1f)] public float aoStrength = 0.85f;

        [Header("Debug")]
        public bool debugLogs = true;
        const string Tag = "[Cliff] ";

        // ---------------------------------------------------------------
        // Structure built once per Build() from the seed
        // ---------------------------------------------------------------

        struct Joint { public float z; public bool cleft; public float depth, width, yLow, yHigh; }
        struct Cave { public float z, depth, halfWidth, height; }
        struct Headland { public float z, protrusion, halfWidth; }

        /// <summary>
        /// fBm's typical output is only about +-0.35, so an "amplitude" setting would act about
        /// 3x smaller than its name. Multiplying by this makes amplitudes mean real metres (peak).
        /// </summary>
        const float NoiseToUnit = 2.5f;

        /// <summary>Everything that depends only on the position along the coast (computed once per column).</summary>
        struct ColumnData
        {
            public float z, top, coastline, beddingWobble, wetEdge;
            public float protrusion;   // 0..1: how far this part of the coast sticks out to sea
        }

        float[] layerTops;
        readonly List<Joint> joints = new List<Joint>();
        readonly List<Cave> caves = new List<Cave>();
        readonly List<Headland> headlands = new List<Headland>();   // sorted by z

        // ---------------------------------------------------------------
        // Unity callbacks (with debounced rebuild in the Editor)
        // ---------------------------------------------------------------

        void OnEnable() => Build();

#if UNITY_EDITOR
        double rebuildAt = -1, lastBuild = -1;

        // Inspector edits: wait until the user stops changing values for 0.5 s, then
        // rebuild once (a rebuild takes a noticeable moment; doing it per edit would freeze).
        void OnValidate()
        {
            rebuildAt = UnityEditor.EditorApplication.timeSinceStartup + 0.5;
            UnityEditor.EditorApplication.update -= RebuildWhenIdle;
            UnityEditor.EditorApplication.update += RebuildWhenIdle;
        }

        void RebuildWhenIdle()
        {
            if (this == null) { UnityEditor.EditorApplication.update -= RebuildWhenIdle; return; }
            if (UnityEditor.EditorApplication.timeSinceStartup < rebuildAt) return;
            UnityEditor.EditorApplication.update -= RebuildWhenIdle;
            // Skip if a build already happened after this change (e.g. OnEnable on scene load).
            if (lastBuild >= rebuildAt - 0.5) return;
            if (isActiveAndEnabled) Build();
        }
#endif

        // ---------------------------------------------------------------
        // Random structure: layers, joints, caves (all from the seed)
        // ---------------------------------------------------------------

        void BuildStructure()
        {
            var rng = new System.Random(seed * 7919 + 13);
            float Range(Vector2 r) => Mathf.Lerp(r.x, r.y, (float)rng.NextDouble());

            var tops = new List<float>();
            for (float y = bottom - 30f; y < top + 30f;) { y += Range(layerThickness); tops.Add(y); }
            layerTops = tops.ToArray();

            joints.Clear();
            for (float z = zMin - 10f; z < zMax + 10f;)
            {
                z += Range(jointSpacing);
                bool cleft = rng.NextDouble() < cleftChance;
                float yA = Mathf.Lerp(bottom, top, (float)rng.NextDouble());
                float yB = Mathf.Lerp(bottom, top, (float)rng.NextDouble());
                joints.Add(new Joint
                {
                    z = z, cleft = cleft,
                    depth = cleft ? Range(cleftDepth) : jointDepth * (0.5f + (float)rng.NextDouble()),
                    width = cleft ? Range(cleftWidth) : 0.35f,
                    yLow = Mathf.Min(yA, yB) - 4f, yHigh = Mathf.Max(yA, yB) + 4f,
                });
            }

            // Headlands: spread evenly over the region with some jitter (so 2-3 fall in the
            // camera's view), each with its own size. Sorted by z for the bay lookup.
            headlands.Clear();
            float spacing = (headlandRegion.y - headlandRegion.x) / headlandCount;
            for (int h = 0; h < headlandCount; h++)
            {
                float z = headlandRegion.x + (h + 0.5f) * spacing + ((float)rng.NextDouble() - 0.5f) * 0.5f * spacing;
                headlands.Add(new Headland { z = z, protrusion = Range(headlandProtrusion), halfWidth = Range(headlandHalfWidth) });
            }

            caves.Clear();
            foreach (Joint j in joints)
            {
                if (caves.Count >= caveCount) break;
                if (j.z < caveRegion.x || j.z > caveRegion.y) continue;   // any joint (clefts are rare now)
                if (caves.Count > 0 && j.z - caves[caves.Count - 1].z < 15f) continue;
                caves.Add(new Cave { z = j.z, depth = Range(caveDepth), halfWidth = Range(caveHalfWidth), height = Range(caveHeight) });
            }
        }

        /// <summary>z of column i, with more columns near the centre (same warp as the water grid).</summary>
        float ColumnZ(int i)
        {
            float c = i / (float)segmentsZ * 2f - 1f;                         // -1..1
            float warped = c * ((1f - columnFocus) + columnFocus * c * c);    // dense in the middle
            return Mathf.Lerp(zMin, zMax, warped * 0.5f + 0.5f);
        }

        ColumnData Column(float z)
        {
            float coast = Coastline(z);
            return new ColumnData
            {
                z = z,
                top = SlowTopAt(z),                                            // S5.8: smooth edge
                coastline = coast,
                // Negative coastline = further out to sea; 0 at the mean line, 1 at ~12 m out.
                protrusion = Mathf.Clamp01(-coast / 12f + 0.3f),
                beddingWobble = 1.2f * Noise3D.Fbm(new Vector3(z * 0.012f, seed * 0.7f, 1f), 3),
                // Splash zone top: where waves hit and splash; uneven, and
                // higher where the cliff protrudes (those parts take the biggest hits).
                wetEdge = splashHeight + 2.5f * Mathf.Clamp01(-coast / 12f + 0.3f)
                        + 1.5f * NoiseToUnit * Noise3D.Fbm(new Vector3(z * 0.04f, 23f, 0f), 2),
            };
        }

        // ---------------------------------------------------------------
        // The shape: how far the rock face sticks out at (z, y)
        // ---------------------------------------------------------------

        /// <summary>
        /// Height of the cliff top at coast position z: a slow rise and fall along the coast.
        /// S5.8: this is now the edge height too (it used to add sharp notches at the edge), so the
        /// ground is smooth right up to the lip.
        /// </summary>
        public float SlowTopAt(float z)
        {
            float slow = skylineAmplitude * NoiseToUnit * Noise3D.Fbm(new Vector3(z * 0.008f, seed * 2.1f, 0.3f), 3);
            return Mathf.Max(top + slow, top - 12f);
        }

        /// <summary>
        /// S5.6: height of the ground inland at (x, z): the slow skyline plus a gentle 2D roll.
        /// It depends on BOTH x and z, so nothing runs in straight lines away from the edge.
        /// </summary>
        public float GroundAt(float x, float z)
        {
            float roll = groundRoll * NoiseToUnit * Noise3D.Fbm(new Vector3(x / groundRollScale, z / groundRollScale, 9f), 3);
            return SlowTopAt(z) + roll;
        }

        /// <summary>x position of the cliff face at (z, y). (Convenience; slower than the cached path.)</summary>
        public float WallX(float z, float y)
        {
            if (layerTops == null) BuildStructure();
            return WallX(Column(z), y, out _, out _, out _);
        }

        /// <summary>
        /// x of the face at height y in a column (z-only values cached in 'col').
        /// Also returns the thin layer, its fraction t, and the pillar (used for colour).
        /// +x = deeper into the rock, -x = further out to sea.
        /// </summary>
        float WallX(in ColumnData col, float y, out int layer, out float t, out int pillar)
        {
            float z = col.z;
            float x = baseX + col.coastline;                                   // 1. coastline

            layer = LayerAt(col, y, out t);                                    // 2. bedding
            float here = LayerRelief(layer, z), above = LayerRelief(layer + 1, z);
            x += Mathf.Lerp(here, above, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, t)));

            // 3. STONES: the face is tiled into irregular stones with a
            //    Voronoi pattern on the (z, y) plane, warped by noise so borders wander. Each stone
            //    is a slightly TILTED PLANE (own set-back + own slope) -> angular facets; the
            //    borders between stones are cracks. Two scales: large blocks and small chips.
            Vector2 q = new Vector2(z, y);
            q.x += warpAmount * NoiseToUnit * Noise3D.Fbm(new Vector3(z * warpScale, y * warpScale, 31f), 2);
            q.y += warpAmount * NoiseToUnit * Noise3D.Fbm(new Vector3(z * warpScale, y * warpScale, 37f), 2);
            x += Stone(q, blockSize, blockRelief, blockTilt, blockCrackDepth, blockCrackWidth, seed * 11 + 3, out pillar);
            x += Stone(q, chipSize, chipRelief, chipTilt, chipCrackDepth, chipCrackWidth, seed * 17 + 5, out _);

            // 4. DEEP CLEFTS: the full-height joints stay as big vertical features.
            int j0 = JointIndexAt(z);
            if (j0 >= 0 && j0 < joints.Count) x += JointGroove(joints[j0], z, y);
            if (j0 + 1 < joints.Count) x += JointGroove(joints[j0 + 1], z, y);

            foreach (Cave cave in caves) x += CaveRecess(cave, z, y);           // 5. caves

            x += roughness * Noise3D.Fbm(new Vector3(z * 0.5f, y * 0.5f, seed * 5f), 3);  // 6. roughness

            // 9. PROFILE: the top juts out, the base is cut back by the waves,
            //    both most where the cliff protrudes (headlands take the hardest hits).
            float cap = capOverhang * (0.5f + col.protrusion) * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(col.top - 10f, col.top - 1f, y));
            x -= cap;                                                          // -x = out to sea
            float undercut = undercutDepth * (0.4f + 0.9f * col.protrusion) * Mathf.Exp(-Square((y - undercutCentre) / undercutHeight));
            x += undercut;                                                     // +x = into the rock
            if (y < -2f) x -= (-2f - y) * 0.6f;                                // talus below the water
            if (y > col.top - 1.5f) x += Square(y - (col.top - 1.5f)) * 0.5f; // round the very edge
            return x;
        }

        /// <summary>
        /// How far the cliff line is set back at z (metres, +x = inland, -x = out to sea).
        /// "Headlands and scalloped bays" (round 4, inspired by the Bunda Cliffs):
        ///  - BAYS: between two neighbouring headlands the coast is carved back as a circular
        ///    arc, as if the sea had scooped it out;
        ///  - HEADLANDS: rounded bulges out to sea (a superellipse from above, ~semicircle),
        ///    blended into the bays with a smooth maximum, so there are no sharp corners;
        ///  - one gentle large bend plus a tiny smooth wobble, so it doesn't look drawn with a compass.
        /// </summary>
        float Coastline(float z)
        {
            // Bay arc: find the two headlands around z; arc = 0 at each headland, deepest midway.
            int k = 0;
            while (k < headlands.Count && headlands[k].z <= z) k++;
            float left = k > 0 ? headlands[k - 1].z : headlands[0].z - (headlands.Count > 1 ? headlands[1].z - headlands[0].z : 80f);
            float right = k < headlands.Count ? headlands[k].z : headlands[headlands.Count - 1].z + (headlands.Count > 1 ? headlands[headlands.Count - 1].z - headlands[headlands.Count - 2].z : 80f);
            float v = Mathf.Clamp((z - left) / Mathf.Max(1f, right - left) * 2f - 1f, -1f, 1f);
            // (1 - v^2)^1.5 instead of a pure circle sqrt(1 - v^2): still round in the middle, but it
            // arrives FLAT at each headland. With the circle, two bays met at a headland with
            // vertical slopes -> a sharp cusp ("poking" tip, seen from above in round 4).
            float bay = bayDepth * Mathf.Pow(1f - v * v, 1.5f);

            // Headland bulges, combined with a smooth maximum (rounded junctions).
            float bulge = 0f;
            foreach (Headland h in headlands)
            {
                float u = Mathf.Abs(z - h.z) / h.halfWidth;
                if (u >= 1.5f) continue;
                float shape = Mathf.Pow(Mathf.Max(0f, 1f - Mathf.Pow(Mathf.Min(u, 1f), headlandShape)), 1f / headlandShape);
                bulge = SmoothMax(bulge, h.protrusion * shape, junctionRounding);
            }

            float bend = coastBend * NoiseToUnit * Noise3D.Fbm(new Vector3(z * 0.0067f, seed * 1.3f, 0.5f), 2);
            float wobble = coastWobble * NoiseToUnit * Noise3D.Fbm(new Vector3(z * 0.08f, seed * 2.3f, 1.5f), 2);
            return bay - bulge + bend + wobble;
        }

        /// <summary>
        /// Smooth maximum of a and b: like max(a, b) but rounded over a width k, so two shapes
        /// blend without a sharp corner (polynomial smooth min/max, Quilez "smooth minimum").
        /// </summary>
        static float SmoothMax(float a, float b, float k)
        {
            float h = Mathf.Max(k - Mathf.Abs(a - b), 0f) / Mathf.Max(k, 1e-5f);
            return Mathf.Max(a, b) + h * h * k * 0.25f;
        }

        /// <summary>Which thin layer a point is in (binary search), and how far up inside it.</summary>
        int LayerAt(in ColumnData col, float y, out float t)
        {
            float ys = y + dip * col.z + col.beddingWobble;
            int lo = 0, hi = layerTops.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (layerTops[mid] <= ys) lo = mid + 1; else hi = mid; }
            if (lo >= layerTops.Length) { t = 0f; return layerTops.Length; }
            float below = lo > 0 ? layerTops[lo - 1] : bottom - 30f;
            t = (ys - below) / (layerTops[lo] - below);
            return lo;
        }

        float LayerRelief(int layer, float z)
        {
            float thin = (Noise3D.Hash(layer * 13 + seed * 101) - 0.5f) * 2f * thinLayerStep;
            int bed = Mathf.FloorToInt(layer / (float)Mathf.Max(1, layersPerBed));
            float bedStep = (Noise3D.Hash(bed * 977 + seed * 59) - 0.5f) * 2f * bedRelief;
            float alongCoast = 0.4f * Noise3D.Fbm(new Vector3(z * 0.02f, bed * 1.7f, seed), 2);
            return thin + bedStep + alongCoast;
        }

        /// <summary>Index of the last joint at or before z (binary search).</summary>
        int JointIndexAt(float z)
        {
            int lo = 0, hi = joints.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (joints[mid].z <= z) lo = mid + 1; else hi = mid; }
            return lo - 1;
        }

        /// <summary>Only deep clefts cut the face now; the regular joints were replaced by stone cells.</summary>
        float JointGroove(Joint j, float z, float y)
        {
            if (!j.cleft) return 0f;
            return j.depth * Mathf.Exp(-Square((z - j.z) / j.width));
        }

        /// <summary>
        /// One layer of stones at position q (metres on the face). size = typical stone size
        /// (z, y); the pattern is evaluated in "stone units" (q / size). Returns the x offset:
        /// the stone's tilted plane plus the crack groove near its border. id = the stone's hash.
        /// </summary>
        float Stone(Vector2 q, Vector2 size, float relief, float tilt, float crackDepth, float crackWidth, int stoneSeed, out int id)
        {
            var cell = Noise3D.Voronoi(new Vector2(q.x / size.x, q.y / size.y), stoneSeed);
            id = cell.id;
            float h0 = Noise3D.Hash(cell.id), h1 = Noise3D.Hash(cell.id + 1), h2 = Noise3D.Hash(cell.id + 2);

            // The stone's face: its own set-back plus a random slope across it (relative to its centre).
            Vector2 centre = new Vector2(cell.point.x * size.x, cell.point.y * size.y);
            float plane = (h0 - 0.5f) * 2f * relief
                        + (h1 - 0.5f) * 2f * tilt * (q.x - centre.x)
                        + (h2 - 0.5f) * 2f * tilt * (q.y - centre.y);

            // The crack: deepest exactly on the border, fading out over crackWidth.
            // (border is in stone units; scale by the smaller size to get metres.)
            float borderMetres = cell.border * Mathf.Min(size.x, size.y);
            float depth = crackDepth * (0.5f + Noise3D.Hash(cell.id + 3));     // cracks vary per stone
            float crack = depth * (1f - Mathf.SmoothStep(0f, 1f, borderMetres / crackWidth));
            return plane + crack;
        }

        float CaveRecess(Cave c, float z, float y)
        {
            float u = (z - c.z) / c.halfWidth;
            if (u * u > 1.5f) return 0f;                                      // quick reject: far from this cave
            float v = (y + 2f) / c.height;
            float inside = v >= 0f ? 1f - u * u - v * v : 1f - u * u - Square(v / 0.4f);
            return c.depth * Mathf.SmoothStep(0f, 1f, inside / 0.6f);   // softer walls: fewer stretched triangles
        }

        // ---------------------------------------------------------------
        // Colour (picked in sRGB, converted to linear when written to the mesh)
        // ---------------------------------------------------------------

        Color ColorAt(in ColumnData col, float y, int layer, int pillar)
        {
            float z = col.z;
            float drift = Noise3D.Fbm(new Vector3(z * 0.02f, y * 0.03f, 11f), 3) * 0.5f + 0.5f;
            Color c = Color.Lerp(slateColor, Color.Lerp(coolShift, warmShift, drift), 0.6f);
            c *= 0.85f + 0.3f * (Noise3D.Fbm(new Vector3(z * 0.05f, y * 0.05f, 13f), 2) * 0.5f + 0.5f);
            c *= 0.9f + 0.2f * Noise3D.Hash(layer * 577 + pillar * 131 + seed);

            // Rust streaks (fBm mapped to 0..1 mostly stays within 0.3-0.7, so thresholds sit inside it).
            float streaks = Noise3D.Fbm(new Vector3(z * 0.25f, y * 0.02f, 17f), 3) * 0.5f + 0.5f;
            float patches = Noise3D.Fbm(new Vector3(z * 0.015f, y * 0.02f, 19f), 2) * 0.5f + 0.5f;
            float rust = Smooth(0.48f, 0.62f, streaks) * Smooth(0.45f, 0.6f, patches) * rustAmount;
            c = Color.Lerp(c, rustColor * (0.8f + 0.4f * streaks), rust);

            // SPLASH ZONE (P4): from the water up to col.wetEdge the rock is wet and
            // mossy. Dense near the water, breaking into patches towards the top edge.
            float zone = 1f - Smooth(col.wetEdge - 2.5f, col.wetEdge, y);      // 1 low, 0 above
            float patches2 = Noise3D.Fbm(new Vector3(z * 0.15f, y * 0.3f, 29f), 3) * 0.5f + 0.5f;
            float moss = zone * Smooth(0.35f - 0.3f * zone, 0.6f - 0.3f * zone, patches2);
            c = Color.Lerp(c, mossColor * (0.8f + 0.4f * patches2), 0.75f * moss);
            // Permanently wet, darker rock just around and below the waterline.
            float wet = 1f - Smooth(-0.5f, 1.5f, y);
            c = Color.Lerp(c, wetColor, 0.6f * wet);
            if (y < -1f) c *= 0.6f;
            return c;
        }

        // ---------------------------------------------------------------
        // Baked ambient occlusion
        // ---------------------------------------------------------------

        float[] BakeOcclusion(float[] depthX, int nz, int ny, float cellZ, float cellY)
        {
            float[] fine = BoxBlur(depthX, nz, ny, Mathf.Max(1, Mathf.RoundToInt(aoFineRadius / cellZ)), Mathf.Max(1, Mathf.RoundToInt(aoFineRadius / cellY)));
            float[] broad = BoxBlur(depthX, nz, ny, Mathf.Max(1, Mathf.RoundToInt(aoBroadRadius / cellZ)), Mathf.Max(1, Mathf.RoundToInt(aoBroadRadius / cellY)));
            var ao = new float[depthX.Length];
            for (int k = 0; k < ao.Length; k++)
            {
                float recessFine = Mathf.SmoothStep(0f, 1f, (depthX[k] - fine[k]) / aoFineDepth);
                float recessBroad = Mathf.SmoothStep(0f, 1f, (depthX[k] - broad[k]) / aoBroadDepth);
                ao[k] = Mathf.Clamp(1f - aoStrength * Mathf.Clamp01(0.5f * recessFine + 0.8f * recessBroad), 0.08f, 1f);
            }
            return ao;
        }

        /// <summary>Separable box blur (running sum, so cost doesn't depend on the radius), edges clamped.</summary>
        static float[] BoxBlur(float[] src, int nz, int ny, int rz, int ry)
        {
            var tmp = new float[src.Length];
            var dst = new float[src.Length];
            Parallel.For(0, ny, j =>
            {
                float sum = 0f;
                for (int i = -rz; i <= rz; i++) sum += src[j * nz + Mathf.Clamp(i, 0, nz - 1)];
                for (int i = 0; i < nz; i++)
                {
                    tmp[j * nz + i] = sum / (2 * rz + 1);
                    sum += src[j * nz + Mathf.Min(i + rz + 1, nz - 1)] - src[j * nz + Mathf.Max(i - rz, 0)];
                }
            });
            Parallel.For(0, nz, i =>
            {
                float sum = 0f;
                for (int j = -ry; j <= ry; j++) sum += tmp[Mathf.Clamp(j, 0, ny - 1) * nz + i];
                for (int j = 0; j < ny; j++)
                {
                    dst[j * nz + i] = sum / (2 * ry + 1);
                    sum += tmp[Mathf.Min(j + ry + 1, ny - 1) * nz + i] - tmp[Mathf.Max(j - ry, 0) * nz + i];
                }
            });
            return dst;
        }

        // ---------------------------------------------------------------
        // Mesh generation
        // ---------------------------------------------------------------

        [ContextMenu("Rebuild Cliff")]
        public void Build()
        {
#if UNITY_EDITOR
            lastBuild = UnityEditor.EditorApplication.timeSinceStartup;
#endif
            var total = System.Diagnostics.Stopwatch.StartNew();
            var step = System.Diagnostics.Stopwatch.StartNew();
            BuildStructure();

            int nz = segmentsZ + 1, ny = segmentsY + 1, nx = segmentsPlateau + 1;
            int faceCount = nz * ny;
            var vertices = new Vector3[faceCount + nz * nx];
            var colors = new Color[vertices.Length];
            var depthX = new float[faceCount];

            // 1. Per-column data (z-only noise), once.
            var columns = new ColumnData[nz];
            Parallel.For(0, nz, i => columns[i] = Column(ColumnZ(i)));
            double tColumns = step.Elapsed.TotalMilliseconds; step.Restart();

            // 2. THE FACE, rows in parallel: each (j, i) writes only its own slot.
            Parallel.For(0, ny, j =>
            {
                for (int i = 0; i < nz; i++)
                {
                    ColumnData col = columns[i];
                    float y = Mathf.Lerp(bottom, col.top, j / (float)segmentsY);
                    float x = WallX(col, y, out int layer, out _, out int pillar);
                    int k = j * nz + i;
                    depthX[k] = x;
                    vertices[k] = new Vector3(x, y, col.z);
                    // Picked as sRGB; Unity passes vertex colours unconverted and we render in Linear.
                    colors[k] = ColorAt(col, y, layer, pillar).linear;
                }
            });
            double tFace = step.Elapsed.TotalMilliseconds; step.Restart();

            // 3. BAKED AO into alpha. (Blur radii use the spacing at the centre; far columns are
            //    wider, so their AO neighbourhood is a little larger, which is fine far away.)
            float cellZ = Mathf.Abs(ColumnZ(nz / 2 + 1) - ColumnZ(nz / 2));
            float cellY = (top - bottom) / segmentsY;
            float[] ao = BakeOcclusion(depthX, nz, ny, cellZ, cellY);
            float aoSum = 0f, aoMin = 1f;
            for (int k = 0; k < faceCount; k++) { colors[k].a = ao[k]; aoSum += ao[k]; aoMin = Mathf.Min(aoMin, ao[k]); }
            double tAO = step.Elapsed.TotalMilliseconds; step.Restart();

            // 4. THE PLATEAU (S5.6: the broken edge blends into smooth, natural ground).
            //    Before S5.6 the height was the notched edge skyline, the same all the way inland, so
            //    every notch of the edge ran 160 m back as a ridge, and the face's jagged top row was
            //    smeared inland as streaks. Now, per vertex:
            //    - xLine: the old straight line from the edge point to the back, with rows packed
            //      densely at the edge (inland = t^2: ~0.1 m apart at the lip, ~8 m at the back);
            //    - xGrid: the same row on a SMOOTH line that follows the coast: it starts at a smoothed
            //      copy of the edge (local maximum over +-gridWindow columns, then averaged, + 0.5 m),
            //      so it never lies seaward of the edge and has no pillars or clefts in it;
            //    - w: 0 at the edge, 1 after topBlend metres inland (smoothstep).
            //    x = lerp(xLine, xGrid, w) and y = lerp(edge skyline, GroundAt(x, z), w).
            //    x never decreases inland (each term grows with the row, and xGrid >= xLine), so rows
            //    can't cross and no triangle folds. Row 0 is exactly the face's top row: no gap.
            //    (First version used ONE straight line behind the most inland edge point of the whole
            //    coast. Behind headlands the edge is ~48 m further out, so the first rows were stretched
            //    across that gap and showed as streaks. Fixed by following the coast locally.)
            const int gridWindow = 30;                                   // columns (~6-15 m)
            float back = baseX + plateauDepth;
            var edgeLocalMax = new float[nz];
            var gridStart = new float[nz];
            for (int i = 0; i < nz; i++)
            {
                float m = float.MinValue;
                for (int j = Mathf.Max(0, i - gridWindow); j <= Mathf.Min(nz - 1, i + gridWindow); j++)
                    m = Mathf.Max(m, vertices[(ny - 1) * nz + j].x);
                edgeLocalMax[i] = m;
            }
            // Average of the local maxima: still >= the edge at i (i lies in each neighbour's window).
            for (int i = 0; i < nz; i++)
            {
                float sum = 0f; int count = 0;
                for (int j = Mathf.Max(0, i - gridWindow); j <= Mathf.Min(nz - 1, i + gridWindow); j++) { sum += edgeLocalMax[j]; count++; }
                gridStart[i] = sum / count + 0.5f;
            }
            for (int kk = 0; kk < nx; kk++)
            {
                float t = kk / (float)segmentsPlateau;
                float inland = t * t;                                   // dense rows at the lip
                for (int i = 0; i < nz; i++)
                {
                    float z = columns[i].z;
                    float edgeX = vertices[(ny - 1) * nz + i].x;
                    float xLine = Mathf.Lerp(edgeX, back, inland);
                    float xGrid = Mathf.Lerp(gridStart[i], back, inland);
                    float w = Mathf.SmoothStep(0f, 1f, (xLine - edgeX) / Mathf.Max(0.01f, topBlend));
                    float x = Mathf.Lerp(xLine, xGrid, w);
                    float y = Mathf.Lerp(columns[i].top, GroundAt(x, z), w);
                    int k = faceCount + kk * nz + i;
                    vertices[k] = new Vector3(x, y, z);
                    Color c = (plateauColor * (0.9f + 0.2f * Noise3D.Perlin(new Vector3(x * 0.1f, z * 0.1f, 3f)))).linear;
                    c.a = 1f;
                    colors[k] = c;
                }
            }

            // 5. TRIANGLES, MESH.
            var triangles = new int[((nz - 1) * (ny - 1) + (nz - 1) * (nx - 1)) * 6];
            int written = AddGrid(triangles, 0, vertices, 0, nz, ny, Vector3.left);       // face looks out to sea
            AddGrid(triangles, written, vertices, faceCount, nz, nx, Vector3.up);          // plateau looks up

            var mesh = new Mesh { name = "CliffMesh", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var filter = GetComponent<MeshFilter>();
            if (filter.sharedMesh != null && filter.sharedMesh.name == "CliffMesh") DestroyImmediate(filter.sharedMesh);
            filter.sharedMesh = mesh;
            double tMesh = step.Elapsed.TotalMilliseconds; step.Restart();

            // 6. COLLIDER: only the band around the waterline (the probes raycast there).
            int bandTriangles = 0;
            var collider = GetComponent<MeshCollider>();
            if (collider != null)
            {
                int j0 = Mathf.Clamp(Mathf.FloorToInt((colliderBandMin - bottom) / (top - bottom) * segmentsY), 0, ny - 2);
                int j1 = Mathf.Clamp(Mathf.CeilToInt((colliderBandMax - bottom) / (top - bottom) * segmentsY), j0 + 1, ny - 1);
                int bandRows = j1 - j0 + 1;
                var bandVertices = new Vector3[nz * bandRows];
                System.Array.Copy(vertices, j0 * nz, bandVertices, 0, bandVertices.Length);
                var bandTris = new int[(nz - 1) * (bandRows - 1) * 6];
                AddGrid(bandTris, 0, bandVertices, 0, nz, bandRows, Vector3.left);
                bandTriangles = bandTris.Length / 3;

                if (collider.sharedMesh != null && collider.sharedMesh.name == "CliffColliderBand") DestroyImmediate(collider.sharedMesh);
                var band = new Mesh { name = "CliffColliderBand", indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.DontSave };
                band.SetVertices(bandVertices);
                band.SetTriangles(bandTris, 0);
                collider.sharedMesh = band;
            }
            double tCollider = step.Elapsed.TotalMilliseconds;

            total.Stop();
            if (debugLogs)
            {
                Vector3[] normals = mesh.normals;
                int seaward = 0;
                for (int v = 0; v < faceCount; v++) if (normals[v].x < 0f) seaward++;
                float minX = float.MaxValue, maxX = float.MinValue, minTop = float.MaxValue, maxTop = float.MinValue;
                for (int v = 0; v < faceCount; v++) { minX = Mathf.Min(minX, vertices[v].x); maxX = Mathf.Max(maxX, vertices[v].x); }
                foreach (ColumnData c in columns) { minTop = Mathf.Min(minTop, c.top); maxTop = Mathf.Max(maxTop, c.top); }
                int clefts = 0; foreach (Joint j in joints) if (j.cleft) clefts++;
                var caveZ = new List<string>(); foreach (Cave c in caves) caveZ.Add(c.z.ToString("F0"));
                Debug.Log(Tag + $"{vertices.Length:N0} vertices, {triangles.Length / 3:N0} triangles, " +
                          $"{layerTops.Length} thin layers, {joints.Count} joints ({clefts} deep clefts), " +
                          $"{caves.Count} caves at z = [{string.Join(", ", caveZ)}], {headlands.Count} headlands at z = [{string.Join(", ", headlands.ConvertAll(h => h.z.ToString("F0")))}], " +
                          $"{100f * seaward / faceCount:F1}% of the face looks out to sea, " +
                          $"face x {minX:F1}..{maxX:F1} m, cliff top {minTop:F1}..{maxTop:F1} m, " +
                          $"AO mean {aoSum / faceCount:F2} min {aoMin:F2}, column spacing {cellZ:F2} m at centre, " +
                          $"collider band {bandTriangles:N0} triangles | took {total.Elapsed.TotalMilliseconds:F0} ms " +
                          $"(columns {tColumns:F0}, face {tFace:F0}, AO {tAO:F0}, mesh {tMesh:F0}, collider {tCollider:F0})", this);
            }
        }

        /// <summary>
        /// Connect a (columns x rows) block of vertices starting at 'first' into triangles
        /// (written into 'triangles' from 'offset'); the winding is picked automatically so the
        /// front faces 'outward'. Returns the new write offset.
        /// </summary>
        static int AddGrid(int[] triangles, int offset, Vector3[] vertices, int first, int columns, int rows, Vector3 outward)
        {
            Vector3 p0 = vertices[first], p1 = vertices[first + 1], p2 = vertices[first + columns];
            bool flip = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), outward) < 0f;
            int t = offset;
            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < columns - 1; c++)
                {
                    int a = first + r * columns + c, b = a + 1, cc = a + columns, d = cc + 1;
                    if (!flip) { triangles[t++] = a; triangles[t++] = b; triangles[t++] = cc; triangles[t++] = b; triangles[t++] = d; triangles[t++] = cc; }
                    else { triangles[t++] = a; triangles[t++] = cc; triangles[t++] = b; triangles[t++] = b; triangles[t++] = cc; triangles[t++] = d; }
                }
            }
            return t;
        }

        static float Square(float x) => x * x;

        /// <summary>
        /// GLSL/HLSL-style smoothstep(edge0, edge1, x): 0 below edge0, 1 above edge1, smooth between.
        /// NOTE: Unity's Mathf.SmoothStep(from, to, t) is DIFFERENT: it interpolates between
        /// 'from' and 'to'. Using it as smoothstep(edge0, edge1, x) was a bug (found in P4).
        /// </summary>
        static float Smooth(float edge0, float edge1, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(edge0, edge1, x));
    }
}
