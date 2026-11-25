// EXPERIMENT E4: how far out from the cliff does visible foam reach?
//
// Menu: Coastal > Experiments > E4 Foam Width   (needs PLAY mode; read-only, takes well under 1 s)
//
// Protocol (fixed before running):
//  - Read the foam map back from the GPU (one RFloat texel = F at one point of water).
//  - Since S5.2 the map is indexed by REST position (where each bit of water sits on a calm sea),
//    so the distance from the rock is measured at rest: d = WallX(z, 0) - x0 (the waterline).
//  - For every 1 m band of d from 0 to 30 m: the fraction of texels with F > 0.03
//    (0.03 = what the water draws at >= 12 % white with floor 0.02 and gain 12).
//  - Foam width = the first band (going out from the rock) whose fraction drops below 10 %.
//  - Rows within 15 m of the map's ends (z) are skipped: the water fades the map out there.
//  - Also reported: open water (d > 40 m) p99 and max, to check the faint layer stays below the
//    floor (so the map's rectangle stays hidden).
// Output: a CSV in <repo>/media/S4.10e/ and a summary in the Console ("[E4]").

using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Coastal.EditorTools
{
    public static class E4FoamWidth
    {
        const float Visible = 0.03f;    // F above this draws >= 12 % white (floor 0.02, gain 12)
        const float WidthRule = 0.10f;  // a band counts as "foamy" while >= 10 % of it is visible foam
        const int Bands = 30;           // 1 m bands, 0..30 m from the waterline
        const float SkipEnds = 15f;     // m at each end of the map (edge fade)

        [MenuItem("Coastal/Experiments/E4 Foam Width")]
        public static void RunFromMenu() => Debug.Log(Run("manual"));

        /// <summary>Measures the foam map now. 'label' names the CSV (e.g. "before", "after").</summary>
        public static string Run(string label)
        {
            if (!Application.isPlaying) return "[E4] ABORT: enter Play mode first.";
            var foam = Object.FindAnyObjectByType<FoamSystem>();
            var cliff = Object.FindAnyObjectByType<CliffGenerator>();
            if (foam == null || cliff == null || foam.FoamTexture == null) return "[E4] ABORT: no foam map or cliff.";

            // 1. Read the map back (the same way as the S4.10c measurement).
            RenderTexture rt = foam.FoamTexture;
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            var F = tex.GetPixelData<float>(0).ToArray();
            Object.Destroy(tex);

            // 2. Texel -> rest position (x0, z0). Map x runs along the texture's width, z along its height.
            int w = rt.width, h = rt.height;
            float minX = foam.centre.x - foam.worldSize.x * 0.5f, minZ = foam.centre.y - foam.worldSize.y * 0.5f;
            float dx = foam.worldSize.x / w, dz = foam.worldSize.y / h;

            var total = new int[Bands]; var visible = new int[Bands];
            var open = new System.Collections.Generic.List<float>();
            int mapVisible = 0, mapCount = 0;
            for (int j = 0; j < h; j++)
            {
                float z = minZ + (j + 0.5f) * dz;
                if (z < minZ + SkipEnds || z > minZ + foam.worldSize.y - SkipEnds) continue;
                float waterline = cliff.WallX(z, 0f);
                for (int i = 0; i < w; i++)
                {
                    float x = minX + (i + 0.5f) * dx;
                    float d = waterline - x;                     // > 0 = out at sea
                    if (d < 0f) continue;                        // inside the rock
                    float f = F[j * w + i];
                    mapCount++; if (f > Visible) mapVisible++;
                    int b = (int)d;
                    if (b < Bands) { total[b]++; if (f > Visible) visible[b]++; }
                    else if (d > 40f) open.Add(f);
                }
            }

            // 3. Width and the CSV.
            var csv = new StringBuilder("distance_m,texels,visible_fraction\n");
            float width = Bands;
            for (int b = 0; b < Bands; b++)
            {
                float frac = total[b] > 0 ? (float)visible[b] / total[b] : 0f;
                csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:0.0000}", b, total[b], frac));
                if (width == Bands && frac < WidthRule) width = b;
            }
            open.Sort();
            float p99 = open.Count > 0 ? open[(int)(open.Count * 0.99f)] : 0f, max = open.Count > 0 ? open[open.Count - 1] : 0f;

            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../media/S4.10e"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"e4_foam_width_{label}.csv"), csv.ToString());

            var s = new StringBuilder();
            s.Append(string.Format(CultureInfo.InvariantCulture,
                "[E4 {0}] sim {1:0} s, tau {2} s, splat r {3} m / strength {4} / outset {5} m. ",
                label, Object.FindAnyObjectByType<CoastalSimulation>().SimTime, foam.decayTime,
                foam.splatRadius, foam.splatStrength, foam.splatOutset));
            s.Append(string.Format(CultureInfo.InvariantCulture,
                "FOAM WIDTH {0} m (first 1 m band with < {1:0}% visible foam). Visible fraction by band 0-1, 2-3, 5-6, 10-11, 15-16, 20-21 m: ",
                width, WidthRule * 100f));
            foreach (int b in new[] { 0, 2, 5, 10, 15, 20 })
                s.Append(string.Format(CultureInfo.InvariantCulture, "{0:0.0}% ", total[b] > 0 ? 100f * visible[b] / total[b] : 0f));
            s.Append(string.Format(CultureInfo.InvariantCulture,
                "| whole coastal map {0:0.0}% visible | open water (d > 40 m) p99 {1:0.0000}, max {2:0.000} (floor 0.02) | splats dropped {3}",
                100f * mapVisible / Mathf.Max(1, mapCount), p99, max, foam.DroppedSplats));
            return s.ToString();
        }
    }
}
