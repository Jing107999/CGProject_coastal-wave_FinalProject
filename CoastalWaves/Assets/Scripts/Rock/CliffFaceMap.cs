using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// A lookup texture of where the cliff face is, so shaders know how close they are to the rock.
    ///
    /// The cliff is a height field on its side: for every coast position z and height y,
    /// CliffGenerator.WallX(z, y) gives the x of the rock face (+x = into the rock). We sample it
    /// once on a (z, y) grid around the waterline and store it in a float texture:
    ///   R = WallX(z, y)
    /// The water shader then gets the distance from any water point to the rock at that point's
    /// own height, which lets it draw contact foam exactly where water touches stone (S4.1).
    /// The run-up on the cliff face (S4.4) uses the same (z, y) layout.
    ///
    /// Globals: _CliffFaceTex, _CliffFaceRect = (zMin, yMin, 1 / (zMax - zMin), 1 / (yMax - yMin)).
    /// </summary>
    public class CliffFaceMap : MonoBehaviour
    {
        public CliffGenerator cliff;

        [Header("Area (around the waterline)")]
        public float zMin = -200f, zMax = 200f;       // S4.10b: the whole coast (was -130..130)
        public float yMin = -6f, yMax = 10f;
        [Tooltip("Samples along the coast (z) and up the face (y).")]
        public int samplesZ = 3150, samplesY = 128;   // ~0.13 m per sample, finer than the cliff mesh

        public bool debugLogs = true;
        const string Tag = "[CliffFace] ";

        /// <summary>The baked face texture (R = x of the rock face).</summary>
        public Texture2D FaceTexture { get; private set; }

        static readonly int FaceTexId = Shader.PropertyToID("_CliffFaceTex");
        static readonly int FaceRectId = Shader.PropertyToID("_CliffFaceRect");

        // Start, not OnEnable: the cliff builds its mesh in ITS OnEnable, and Start runs after all of
        // them (the probe lesson from S2.2).
        void Start() => Bake();

        void OnDestroy()
        {
            if (FaceTexture != null) Destroy(FaceTexture);
            Shader.SetGlobalVector(FaceRectId, Vector4.zero);   // tells shaders "no face map"
        }

        [ContextMenu("Bake")]
        public void Bake()
        {
            if (cliff == null) { Debug.LogError(Tag + "No cliff assigned.", this); return; }
            var timer = System.Diagnostics.Stopwatch.StartNew();

            if (FaceTexture == null || FaceTexture.width != samplesZ || FaceTexture.height != samplesY)
            {
                if (FaceTexture != null) Destroy(FaceTexture);
                FaceTexture = new Texture2D(samplesZ, samplesY, TextureFormat.RFloat, false, true)
                {
                    name = "CliffFaceMap",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            // Texel centres: z = zMin + (i + 0.5) / samplesZ * (zMax - zMin), same for y.
            var data = new float[samplesZ * samplesY];
            float minX = float.MaxValue, maxX = float.MinValue;
            // Rows in parallel: the cliff's shape functions are pure (no shared state is written).
            cliff.WallX(0f, 0f);   // make sure the cliff's cached structure exists before threads start
            int nz = samplesZ, ny = samplesY;
            float z0 = zMin, z1 = zMax, y0 = yMin, y1 = yMax;
            System.Threading.Tasks.Parallel.For(0, ny, j =>
            {
                float y = Mathf.Lerp(y0, y1, (j + 0.5f) / ny);
                for (int i = 0; i < nz; i++)
                    data[j * nz + i] = cliff.WallX(Mathf.Lerp(z0, z1, (i + 0.5f) / nz), y);
            });
            foreach (float x in data) { minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x); }
            FaceTexture.SetPixelData(data, 0);
            FaceTexture.Apply(false, false);

            Shader.SetGlobalTexture(FaceTexId, FaceTexture);
            Shader.SetGlobalVector(FaceRectId, new Vector4(zMin, yMin, 1f / (zMax - zMin), 1f / (yMax - yMin)));

            if (debugLogs)
                Debug.Log(Tag + $"Baked {samplesZ}x{samplesY} over z {zMin}..{zMax} m, y {yMin}..{yMax} m " +
                          $"(face x {minX:F1}..{maxX:F1} m) in {timer.ElapsedMilliseconds} ms", this);
        }
    }
}
