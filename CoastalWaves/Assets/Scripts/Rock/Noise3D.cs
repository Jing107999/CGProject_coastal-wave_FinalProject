using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// Smooth pseudo-random noise for natural shapes (the cliff, later rocks and seabed).
    ///
    /// Perlin(p): "gradient noise". The space is divided into unit cubes; each cube corner
    /// gets a pseudo-random gradient direction. The value at p blends the corners'
    /// contributions with a smooth fade curve, so it changes gradually (no sharp jumps).
    /// Output roughly in [-1, 1]; exactly 0 at integer coordinates.
    ///
    /// Fbm(p): fractional Brownian motion = Perlin added up at several scales ("octaves"),
    /// each with double the frequency and half the amplitude: big shapes + fine detail.
    ///
    /// Deterministic: the permutation table is built from a fixed seed, so the same input
    /// always gives the same output (reproducible scenes).
    ///
    /// Sources: the algorithm follows Perlin's "improved noise": the 6t^5-15t^4+10t^3 fade
    /// curve and the 12-gradient hash trick in Grad() (Perlin 2002, "Improving Noise",
    /// ACM TOG 21(3), and his reference implementation). Original noise: Perlin 1985.
    /// The permutation is shuffled with the Fisher-Yates algorithm (Knuth, TAOCP vol. 2).
    /// Hash() is the "pcg_hash" integer hash recommended by Jarzynski & Olano 2020,
    /// "Hash Functions for GPU Rendering" (JCGT 9(3)), based on PCG (O'Neill 2014).
    /// </summary>
    public static class Noise3D
    {
        // 512 entries = the 256-entry permutation written twice, so perm[i + 1] never overflows.
        static readonly int[] perm = BuildPermutation(1337);

        static int[] BuildPermutation(int seed)
        {
            var p = new int[256];
            for (int i = 0; i < 256; i++) p[i] = i;
            var rng = new System.Random(seed);
            for (int i = 255; i > 0; i--)            // Fisher-Yates shuffle
            {
                int j = rng.Next(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            var table = new int[512];
            for (int i = 0; i < 512; i++) table[i] = p[i & 255];
            return table;
        }

        // 6t^5 - 15t^4 + 10t^3: smooth S-curve with zero slope AND zero curvature at 0 and 1,
        // so neighbouring cubes join without visible creases.
        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        // Dot product of the offset (x, y, z) with one of 12 edge-direction gradients,
        // chosen by the hash (the classic "improved noise" trick, no gradient table needed).
        static float Grad(int hash, float x, float y, float z)
        {
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        /// <summary>Gradient (Perlin) noise at p, roughly in [-1, 1].</summary>
        public static float Perlin(Vector3 p)
        {
            // Which unit cube, and where inside it.
            int xi = Mathf.FloorToInt(p.x), yi = Mathf.FloorToInt(p.y), zi = Mathf.FloorToInt(p.z);
            float x = p.x - xi, y = p.y - yi, z = p.z - zi;
            xi &= 255; yi &= 255; zi &= 255;

            float u = Fade(x), v = Fade(y), w = Fade(z);

            // Hash the 8 cube corners.
            int a = perm[xi] + yi, aa = perm[a] + zi, ab = perm[a + 1] + zi;
            int b = perm[xi + 1] + yi, ba = perm[b] + zi, bb = perm[b + 1] + zi;

            // Blend the corner contributions (trilinear interpolation with the fade weights).
            return Mathf.Lerp(
                Mathf.Lerp(Mathf.Lerp(Grad(perm[aa], x, y, z), Grad(perm[ba], x - 1, y, z), u),
                           Mathf.Lerp(Grad(perm[ab], x, y - 1, z), Grad(perm[bb], x - 1, y - 1, z), u), v),
                Mathf.Lerp(Mathf.Lerp(Grad(perm[aa + 1], x, y, z - 1), Grad(perm[ba + 1], x - 1, y, z - 1), u),
                           Mathf.Lerp(Grad(perm[ab + 1], x, y - 1, z - 1), Grad(perm[bb + 1], x - 1, y - 1, z - 1), u), v),
                w);
        }

        /// <summary>
        /// Fractional Brownian motion: 'octaves' layers of Perlin noise, each 'lacunarity'
        /// times finer and 'gain' times weaker. Normalised to roughly [-1, 1].
        /// </summary>
        public static float Fbm(Vector3 p, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amplitude = 1f, total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += amplitude * Perlin(p);
                total += amplitude;
                p *= lacunarity;
                amplitude *= gain;
            }
            return sum / total;
        }

        /// <summary>
        /// Ridged noise in [0, 1]: (1 - |Perlin|)^2 summed over octaves. |Perlin| is 0 along
        /// thin "valleys" where the noise crosses zero, so 1 - |Perlin| turns them into
        /// sharp ridges, which read as cracks and crease lines in rock.
        /// (Ridged multifractal idea: Musgrave, in Ebert et al., "Texturing and Modeling:
        /// A Procedural Approach", 3rd ed., 2003.)
        /// </summary>
        public static float Ridged(Vector3 p, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amplitude = 1f, total = 0f;
            for (int i = 0; i < octaves; i++)
            {
                float ridge = 1f - Mathf.Abs(Perlin(p));
                sum += amplitude * ridge * ridge;   // squaring sharpens the ridge
                total += amplitude;
                p *= lacunarity;
                amplitude *= gain;
            }
            return sum / total;
        }

        /// <summary>Result of a 2D Voronoi (cellular) lookup.</summary>
        public struct CellSample
        {
            public int id;            // hash of the nearest cell (stable random number per stone)
            public Vector2 point;     // the nearest cell's feature point (same units as the input)
            public float border;      // distance from the query point to the nearest cell border
        }

        /// <summary>
        /// 2D cellular (Voronoi) noise (Worley 1996): random feature points, one per unit grid
        /// cell (jittered); the query belongs to the cell of its nearest point. Also returns the
        /// EXACT distance to the nearest cell border, following the method described by Quilez
        /// ("Voronoi edges", 2012): find the nearest point a, then for every other nearby point b
        /// the border is the bisector between a and b; the distance from the query to it is
        /// dot((a + b) / 2, normalize(b - a)) when positions are relative to the query.
        /// (F2 - F1 would only approximate this.) Our own implementation from that description.
        /// </summary>
        public static CellSample Voronoi(Vector2 p, int seed, float jitter = 0.9f)
        {
            int nx = Mathf.FloorToInt(p.x), ny = Mathf.FloorToInt(p.y);
            float fx = p.x - nx, fy = p.y - ny;

            // Pass 1: the nearest feature point in the 3x3 neighbourhood.
            float best = float.MaxValue; int bi = 0, bj = 0; Vector2 a = Vector2.zero;
            for (int j = -1; j <= 1; j++)
                for (int i = -1; i <= 1; i++)
                {
                    Vector2 r = new Vector2(i + jitter * Hash(Cell(nx + i, ny + j, seed)) - fx,
                                            j + jitter * Hash(Cell(nx + i, ny + j, seed + 7)) - fy);
                    float d = r.sqrMagnitude;
                    if (d < best) { best = d; bi = i; bj = j; a = r; }
                }

            // Pass 2: distance to the nearest bisector, searching 5x5 around the nearest cell.
            float border = float.MaxValue;
            for (int j = -2; j <= 2; j++)
                for (int i = -2; i <= 2; i++)
                {
                    int ci = nx + bi + i, cj = ny + bj + j;
                    Vector2 b = new Vector2(bi + i + jitter * Hash(Cell(ci, cj, seed)) - fx,
                                            bj + j + jitter * Hash(Cell(ci, cj, seed + 7)) - fy);
                    Vector2 ab = b - a;
                    if (ab.sqrMagnitude < 1e-8f) continue;           // that's the nearest point itself
                    border = Mathf.Min(border, Vector2.Dot(0.5f * (a + b), ab.normalized));
                }

            int cx = nx + bi, cy = ny + bj;
            return new CellSample
            {
                id = Cell(cx, cy, seed + 13),
                point = new Vector2(p.x + a.x, p.y + a.y),
                border = border,
            };
        }

        // Combine two integer coordinates and a seed into one hash input.
        static int Cell(int x, int y, int seed) { unchecked { return x * 73856093 ^ y * 19349663 ^ seed * 83492791; } }

        /// <summary>A pseudo-random number in [0, 1) for an integer (same input -> same output).</summary>
        public static float Hash(int n)
        {
            unchecked
            {
                uint h = (uint)n * 747796405u + 2891336453u;   // PCG-style integer hash
                h = ((h >> (int)((h >> 28) + 4u)) ^ h) * 277803737u;
                h = (h >> 22) ^ h;
                return (h & 0x00FFFFFF) / 16777216f;
            }
        }
    }
}
