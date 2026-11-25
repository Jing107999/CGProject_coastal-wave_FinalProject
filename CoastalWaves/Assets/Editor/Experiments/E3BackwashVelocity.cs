// EXPERIMENT E3: how fast does the water move back out to sea in front of the cliff?
//
// Menu: Coastal > Experiments > E3 Backwash Velocity   (needs PLAY mode, runs in a few seconds)
//
// Why: wall reflection (method of images) turns the incoming waves into a partial standing wave
// near the cliff. Theory says the water right at a reflecting wall moves LESS horizontally (a
// velocity node for a rigid wall) and the strongest back-and-forth flow sits about a quarter
// wavelength out. This test measures that flow in our wave model and compares it with the formula.
// Gerstner waves are kinematic, so this VERIFIES the implementation; it doesn't simulate real backwash.
//
// Protocol (fixed before running):
//  - Quantity: u_n = v . n, the particle velocity along the wall normal n (n points into the rock,
//    so seaward = -u_n), from WaveField.Evaluate for the particle resting at distance d in front
//    of the mirror plane (no inversion, so no inversion error).
//  - d = 0 .. 120 m in 1 m steps, on three lines z = 0, 40, 80 m (pooled); t = 0 .. 1200 s, dt = 0.05 s.
//  - Per d: RMS of u_n, peak seaward speed max(-u_n), fraction of samples moving seaward.
//  - Analytic RMS from the same component list:
//        RMS^2 = 1/2 * sum_i (a w (D.n))^2 * (1 + R^2 W^2 - 2 R W cos(2 k (D.n) d)),  W = exp(-d / L)
//    (R = 0 for a wave without an image).
//  - Cases: C1 the scene's sea and reflection; C2 the same with reflection off (control);
//    C3 one 70 m wave along +x with R = 1 and no fade (textbook rigid wall).
//  - Everything runs on a private, INACTIVE copy of the WaveField (DontSave, destroyed afterwards).
//    The live field is only read, and re-uploaded to the GPU at the end (the copy's Rebuild
//    overwrites the shader globals while the test runs).
// Output: a CSV in <repo>/media/S5.1/ and a summary with pass/fail in the Console ("[E3]").

using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Coastal.EditorTools
{
    public static class E3BackwashVelocity
    {
        const float MaxDistance = 120f;    // m in front of the mirror plane
        const float DistanceStep = 1f;     // m
        static readonly float[] Lines = { 0f, 40f, 80f }; // z positions (m) of the three sample lines
        const float Duration = 1200f;      // s; closest frequency pair (0.195 rad/s) averages out to ~0.4 %
        const float TimeStep = 0.05f;      // s; shortest wave period is ~1.1 s, so ~20 samples per period
        const float Tolerance = 0.02f;     // 2 % (decision rule)

        struct CaseResult { public bool pass; public string note; }

        [MenuItem("Coastal/Experiments/E3 Backwash Velocity")]
        public static void RunFromMenu() => Debug.Log(Run());

        /// <summary>Runs the three cases. Returns the summary (also usable from an eval).</summary>
        public static string Run()
        {
            // Guard: never touch the Edit-mode scene (values changed there get saved).
            if (!Application.isPlaying) return "[E3] ABORT: enter Play mode first.";
            var live = Object.FindAnyObjectByType<WaveField>();
            if (live == null) return "[E3] ABORT: no WaveField in the scene.";

            var csv = new StringBuilder("case,d_m,rms_measured_mps,rms_analytic_mps,rel_error,max_seaward_mps,frac_seaward\n");
            var summary = new StringBuilder();
            summary.AppendLine("[E3] Backwash velocity: u_n along the wall normal vs distance d from the mirror plane");
            summary.AppendLine($"     {Lines.Length} lines x {Mathf.RoundToInt(Duration / TimeStep)} samples per distance, " +
                               $"d = 0..{MaxDistance:0} m");

            // The private copy. Inactive BEFORE AddComponent, so OnEnable never runs on it.
            var go = new GameObject("E3_WaveField") { hideFlags = HideFlags.DontSave };
            go.SetActive(false);
            try
            {
                var copy = go.AddComponent<WaveField>();

                // C1: exactly the scene's settings.
                CopySettings(live, copy);
                copy.Rebuild();
                CaseResult c1 = RunCase("C1_scene_R0.5", copy, csv, summary, checkNodes: false);

                // C2: same sea, reflection off. The profile must be flat.
                copy.reflection = false;
                copy.Rebuild();
                CaseResult c2 = RunCase("C2_no_reflection", copy, csv, summary, checkNodes: false);

                // C3: textbook rigid wall: one 70 m wave straight at the wall, full reflection, no fade.
                copy.waves = new[] { new GerstnerWave(0f, 70f, 0.17f, 0f) };
                copy.steepnessScale = 1f;
                copy.reflection = true;
                copy.reflectionCoefficient = 1f;
                copy.minReflectedWavelength = 0f;
                copy.reflectionDecay = 1e9f;   // W = exp(-d / 1e9) = 1 everywhere we sample
                copy.Rebuild();
                CaseResult c3 = RunCase("C3_rigid_wall_70m", copy, csv, summary, checkNodes: true);

                // Decision rule, in the order fixed in the plan: the control first.
                summary.AppendLine("Decision rule:");
                summary.AppendLine($"  1. C2 control   : {(c2.pass ? "PASS" : "FAIL")} {c2.note}");
                summary.AppendLine($"  2. C3 textbook  : {(c3.pass ? "PASS" : "FAIL")} {c3.note}");
                summary.AppendLine($"  3. C1 result    : {(c1.pass ? "PASS" : "FAIL")} {c1.note}" +
                                   (c2.pass ? "" : "  (not interpreted: the control failed)"));

                string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../media/S5.1"));
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "e3_backwash_velocity.csv");
                File.WriteAllText(path, csv.ToString());
                summary.AppendLine("CSV: " + path);
            }
            finally
            {
                Object.Destroy(go);  // Play mode: Destroy, not DestroyImmediate
                live.Upload();       // put the live sea back on the GPU
            }
            return summary.ToString();
        }

        /// <summary>Copy every setting that changes the wave formula. Logging is switched off.</summary>
        static void CopySettings(WaveField from, WaveField to)
        {
            to.waves = (GerstnerWave[])from.waves.Clone();
            to.steepnessScale = from.steepnessScale;
            to.seaLevel = from.seaLevel;
            to.reflection = from.reflection;
            to.wallNormal = from.wallNormal;
            to.wallOffset = from.wallOffset;
            to.reflectionCoefficient = from.reflectionCoefficient;
            to.minReflectedWavelength = from.minReflectedWavelength;
            to.reflectionDecay = from.reflectionDecay;
            to.debugLogs = false;
        }

        /// <summary>Measure the profile for one wave field, write CSV rows, check against the formula.</summary>
        static CaseResult RunCase(string name, WaveField field, StringBuilder csv, StringBuilder summary, bool checkNodes)
        {
            Vector2 n = field.wallNormal.sqrMagnitude > 1e-6f ? field.wallNormal.normalized : Vector2.right;
            Vector2 tangent = new Vector2(-n.y, n.x);                 // along the wall, for the three lines
            int steps = Mathf.RoundToInt(Duration / TimeStep);
            int distances = Mathf.RoundToInt(MaxDistance / DistanceStep) + 1;

            float worstError = 0f, worstAt = 0f, maxRms = 0f, maxRmsAt = 0f, rmsAtWall = 0f;
            bool pass = true;
            string fail = "";

            for (int di = 0; di < distances; di++)
            {
                float d = di * DistanceStep;
                double sumSq = 0.0;
                float maxSeaward = 0f;
                long seaward = 0, count = 0;

                foreach (float line in Lines)
                {
                    // Rest position d metres in front of the plane n . x = c, shifted 'line' metres along it.
                    float x0 = n.x * (field.wallOffset - d) + tangent.x * line;
                    float z0 = n.y * (field.wallOffset - d) + tangent.y * line;
                    for (int s = 0; s < steps; s++)
                    {
                        Vector3 v = field.Evaluate(x0, z0, s * TimeStep).velocity;
                        float un = v.x * n.x + v.z * n.y;            // + = into the rock, - = seaward
                        sumSq += (double)un * un;
                        if (-un > maxSeaward) maxSeaward = -un;
                        if (un < 0f) seaward++;
                        count++;
                    }
                }

                float rms = (float)System.Math.Sqrt(sumSq / count);
                float theory = AnalyticRms(field, n, d);
                float relError = theory > 1e-6f ? Mathf.Abs(rms - theory) / theory : 0f;

                // Pass checks (the plan's rule): relative error where the velocity is not tiny;
                // for C3, an absolute check at the nodes and the peak speed at the antinode.
                bool checkRelative = !checkNodes || theory >= 0.1f;
                if (checkRelative && relError > Tolerance)
                {
                    if (pass) fail = $"first miss at d = {d:0} m: {rms:0.000} vs {theory:0.000} m/s";
                    pass = false;
                }
                if (checkRelative && relError > worstError) { worstError = relError; worstAt = d; }
                if (checkNodes && (Mathf.Approximately(d, 0f) || Mathf.Approximately(d, 35f)) && rms > 0.01f)
                {
                    if (pass) fail = $"node at d = {d:0} m not still: RMS {rms:0.0000} m/s";
                    pass = false;
                }
                if (checkNodes && Mathf.Approximately(d, 17f))
                {
                    // Single wave: peak seaward speed = 2 a w |sin(k d)|.
                    WaveField.Component c = field.Components[0];
                    float peak = 2f * c.a * c.omega * Mathf.Abs(Mathf.Sin(c.k * d));
                    float peakError = Mathf.Abs(maxSeaward - peak) / peak;
                    summary.AppendLine($"     {name}: peak seaward speed at d = 17 m: {maxSeaward:0.000} m/s " +
                                       $"(theory {peak:0.000}, error {peakError * 100f:0.00} %)");
                    if (peakError > Tolerance)
                    {
                        if (pass) fail = $"peak seaward speed at 17 m off by {peakError * 100f:0.0} %";
                        pass = false;
                    }
                }

                if (di == 0) rmsAtWall = rms;
                if (rms > maxRms) { maxRms = rms; maxRmsAt = d; }

                csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:0.0},{2:0.00000},{3:0.00000},{4:0.00000},{5:0.0000},{6:0.0000}",
                    name, d, rms, theory, relError, maxSeaward, (double)seaward / count));
            }

            summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "     {0}: RMS at the wall {1:0.000} m/s, largest {2:0.000} m/s at d = {3:0} m, worst error {4:0.00} % at d = {5:0} m",
                name, rmsAtWall, maxRms, maxRmsAt, worstError * 100f, worstAt));

            return new CaseResult
            {
                pass = pass,
                note = pass ? $"(worst {worstError * 100f:0.00} % at d = {worstAt:0} m)" : "(" + fail + ")",
            };
        }

        /// <summary>
        /// Theory: RMS of u_n at distance d. Each incident wave pairs with its image (same k and w,
        /// direction mirrored); the pair is one sinusoid of amplitude
        ///     a w (D.n) sqrt(1 + R^2 W^2 - 2 R W cos(2 k (D.n) d)).
        /// Different waves have different frequencies, so their squares add.
        /// </summary>
        static float AnalyticRms(WaveField field, Vector2 n, float d)
        {
            WaveField.Component[] comps = field.Components;
            float window = Mathf.Exp(-d / Mathf.Max(1f, field.reflectionDecay)); // the field's own fade W
            double sumSq = 0.0;

            foreach (WaveField.Component c in comps)
            {
                if (c.windowed) continue;                          // images are handled with their incident wave
                float dn = c.dx * n.x + c.dz * n.y;                // D . n
                float amp = c.a * c.omega * dn;                    // incident amplitude of u_n

                // Find this wave's image, if it has one: same k and w, mirrored direction.
                float R = 0f;
                foreach (WaveField.Component img in comps)
                {
                    if (!img.windowed || !Mathf.Approximately(img.k, c.k)) continue;
                    float imgDn = img.dx * n.x + img.dz * n.y;
                    if (Mathf.Abs(imgDn + dn) < 1e-4f) { R = img.a / c.a; break; }
                }

                float rw = R * window;
                double factor = 1.0 + rw * rw - 2.0 * rw * System.Math.Cos(2.0 * c.k * dn * d);
                sumSq += amp * amp * factor;
            }
            return (float)System.Math.Sqrt(0.5 * sumSq);
        }
    }
}
