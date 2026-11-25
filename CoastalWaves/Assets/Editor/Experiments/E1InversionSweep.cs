// EXPERIMENT E1: how many fixed-point iterations does the height query need?
//
// Menu: Coastal > Experiments > E1 Inversion Sweep   (runs in Edit mode, ~1-2 s)
//
// Protocol (fixed before running):
//  - 2,000 random (p, t): p uniform in a 200 m x 200 m square, t uniform in [0, 600] s, seed 1234.
//  - Ground truth: 50 iterations (its residual is reported to show it has converged).
//  - For n = 0..8: height error |y(n) - y(50)| (mean, 95th percentile, max),
//    residual |x0 + H(x0) - p| (mean, max), convergence rate (ratio of mean residuals),
//    and cost per HeightAt call (µs, 20,000 calls).
//  - Repeated for steepness scales 0.5, 1.0, 1.25, 1.3 (total steepness 0.38 ... 0.98).
// Output: a CSV in <repo>/media/S1.5/ and a summary table in the Console ("[E1]").

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Coastal.EditorTools
{
    public static class E1InversionSweep
    {
        const int Points = 2000;         // random test points
        const int MaxIterations = 8;     // n = 0 .. 8
        const int TruthIterations = 50;  // treated as the converged "ground truth"
        const int TimingCalls = 20000;   // calls per cost measurement
        const int Seed = 1234;           // fixed, so every run uses the same points
        static readonly float[] SteepnessScales = { 0.5f, 1.0f, 1.25f, 1.3f };

        [MenuItem("Coastal/Experiments/E1 Inversion Sweep")]
        public static void Run()
        {
            var field = Object.FindAnyObjectByType<WaveField>();
            if (field == null) { Debug.LogError("[E1] No WaveField in the open scene."); return; }

            // Remember settings so the experiment leaves the scene exactly as it found it.
            float originalScale = field.steepnessScale;
            bool originalLogs = field.debugLogs;
            field.debugLogs = false; // Rebuild() would otherwise log for every steepness

            // 1. The test set: the same random points and times for every configuration.
            var rng = new System.Random(Seed);
            var px = new float[Points]; var pz = new float[Points]; var pt = new float[Points];
            for (int i = 0; i < Points; i++)
            {
                px[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
                pz[i] = (float)(rng.NextDouble() * 200.0 - 100.0);
                pt[i] = (float)(rng.NextDouble() * 600.0);
            }

            var csv = new StringBuilder("steepness_scale,total_steepness,iterations," +
                                        "height_err_mean_m,height_err_p95_m,height_err_max_m," +
                                        "residual_mean_m,residual_max_m,convergence_rate,us_per_call\n");
            var summary = new StringBuilder("[E1] Inversion sweep (2,000 points, truth = 50 iterations)\n");

            try
            {
                foreach (float scale in SteepnessScales)
                {
                    field.steepnessScale = scale;
                    field.Rebuild();
                    summary.AppendLine($"--- steepness scale {scale:F2} (total steepness {field.TotalSteepness:F3}) ---");

                    // 2. Ground truth for every point.
                    var truthY = new float[Points];
                    double truthResidualMax = 0;
                    for (int i = 0; i < Points; i++)
                    {
                        Vector2 rest = field.FindRestPosition(px[i], pz[i], pt[i], TruthIterations);
                        truthY[i] = field.Evaluate(rest.x, rest.y, pt[i]).position.y;
                        truthResidualMax = System.Math.Max(truthResidualMax, Residual(field, rest, px[i], pz[i], pt[i]));
                    }
                    summary.AppendLine($"    truth check: max residual after {TruthIterations} iterations = {truthResidualMax:E1} m");
                    summary.AppendLine("     n |  height err mean    p95       max   | residual mean    max    | rate  | µs/call");

                    // 3. Each iteration count.
                    double previousResidualMean = double.NaN;
                    for (int n = 0; n <= MaxIterations; n++)
                    {
                        var errors = new double[Points];
                        double residualSum = 0, residualMax = 0;
                        for (int i = 0; i < Points; i++)
                        {
                            Vector2 rest = field.FindRestPosition(px[i], pz[i], pt[i], n);
                            float y = field.Evaluate(rest.x, rest.y, pt[i]).position.y;
                            errors[i] = System.Math.Abs(y - truthY[i]);
                            double r = Residual(field, rest, px[i], pz[i], pt[i]);
                            residualSum += r;
                            residualMax = System.Math.Max(residualMax, r);
                        }
                        System.Array.Sort(errors);
                        double mean = 0; foreach (double e in errors) mean += e; mean /= Points;
                        double p95 = errors[(int)(0.95 * (Points - 1))];
                        double max = errors[Points - 1];
                        double residualMean = residualSum / Points;
                        double rate = residualMean / previousResidualMean; // how much the error shrank this round
                        previousResidualMean = residualMean;

                        // 4. Cost: time many HeightAt calls with n iterations.
                        float sink = 0f;
                        var stopwatch = Stopwatch.StartNew();
                        for (int k = 0; k < TimingCalls; k++)
                        {
                            int i = k % Points;
                            sink += field.HeightAt(px[i], pz[i], pt[i], n);
                        }
                        stopwatch.Stop();
                        double microseconds = stopwatch.Elapsed.TotalMilliseconds * 1000.0 / TimingCalls;
                        if (float.IsNaN(sink)) Debug.LogWarning("[E1] NaN in timing loop"); // also stops the loop being optimised away

                        csv.AppendLine(string.Join(",",
                            F(scale), F(field.TotalSteepness), n.ToString(CultureInfo.InvariantCulture),
                            F(mean), F(p95), F(max), F(residualMean), F(residualMax),
                            double.IsNaN(rate) ? "" : F(rate), F(microseconds)));

                        summary.AppendLine($"    {n,2} | {mean * 1000,9:F4} mm {p95 * 1000,9:F4} {max * 1000,9:F3} | " +
                                           $"{residualMean * 1000,9:F4} mm {residualMax * 1000,8:F3} | " +
                                           $"{(double.IsNaN(rate) ? "  -  " : rate.ToString("F3"))} | {microseconds,6:F2}");
                    }
                }

                // 5. Save the CSV next to the other media for this step.
                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "media", "S1.5"));
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "E1_inversion_sweep.csv");
                File.WriteAllText(path, csv.ToString());
                summary.AppendLine($"CSV written to {path}");
                Debug.Log(summary.ToString());
            }
            finally
            {
                // Always restore the scene's wave settings, even if something failed.
                field.steepnessScale = originalScale;
                field.Rebuild();
                field.debugLogs = originalLogs;
            }
        }

        // How far the particle found from 'rest' lands from the target point p (metres).
        static double Residual(WaveField field, Vector2 rest, float x, float z, float t)
        {
            Vector2 h = field.HorizontalShift(rest.x, rest.y, t);
            double dx = rest.x + h.x - x, dz = rest.y + h.y - z;
            return System.Math.Sqrt(dx * dx + dz * dz);
        }

        // Numbers in the CSV always use '.' as decimal separator, whatever the computer's language.
        static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }
}
