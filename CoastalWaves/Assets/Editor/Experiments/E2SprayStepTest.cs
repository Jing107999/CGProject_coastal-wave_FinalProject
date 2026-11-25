// EXPERIMENT E2: does the spray's air drag depend on the frame rate?
//
// Menu: Coastal > Experiments > E2 Spray Step Test   (needs PLAY mode, runs in well under 1 s)
//
// Why: the Plume and Mist spray layers are slowed by Unity's "Limit Velocity over Lifetime"
// module (speed above a limit is damped by a fraction). We don't know whether Unity scales that
// fraction by the frame time. If it doesn't, a 30 fps recording and a 240 fps Editor run would
// show different spray: different plume heights and fall speeds.
//
// Protocol (fixed before running):
//  - Copy a live spray layer (Plume or Mist) so the test uses exactly the scene's settings,
//    then switch off its turbulence (random, so it would hide the effect) and its renderer.
//  - Launch ONE particle straight up from y = 1 m with a fixed speed and lifetime.
//  - Advance the copy by hand with ParticleSystem.Simulate(dt) in equal steps:
//    dt = 1/10, 1/30, 1/60, 1/120, 1/240 s (1/10 is a stress case only).
//    Simulating by hand makes the test deterministic and immune to the Editor's own frame rate
//    (and to the Editor freezing when it loses focus).
//  - Each step: read the particle's height; vertical speed = change in height / dt.
//  - Per run: apex height, time of the apex, fall speed at 50 % and 75 % of the life,
//    time the particle died (hit the sea-level plane) if before the end of its life.
//  - Cases: Plume at 10 m/s (strongest hit) and 3 m/s (weakest), Mist at 4 m/s.
//  - The copies are destroyed afterwards and flagged DontSave, so nothing reaches the scene.
// Output: a CSV in <repo>/media/S4.9/ (height every 0.1 s) and a summary table in the Console ("[E2]").

using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Coastal.EditorTools
{
    public static class E2SprayStepTest
    {
        // Step sizes (s). 1/240 is our baseline (close to the uncapped Editor rate, 140-300 fps).
        static readonly float[] Steps = { 1f / 10f, 1f / 30f, 1f / 60f, 1f / 120f, 1f / 240f };
        const float LaunchHeight = 1f;   // m, above the sea-level kill plane at y = 0
        const float SampleEvery = 0.1f;  // s, CSV resolution (a whole number of steps for every dt above)

        struct Case
        {
            public string layer; public float speed, lifetime;
            public Case(string layer, float speed, float lifetime) { this.layer = layer; this.speed = speed; this.lifetime = lifetime; }
        }

        // Speeds = the ends of each layer's launch range; lifetimes = the middle of its lifetime range.
        static readonly Case[] Cases =
        {
            new Case("Plume", 10f, 4f),
            new Case("Plume", 3f, 4f),
            new Case("Mist", 4f, 8f),
        };

        [MenuItem("Coastal/Experiments/E2 Spray Step Test")]
        public static void RunFromMenu() => Debug.Log(Run());

        /// <summary>Runs every case at every step size. Returns the summary (also usable from an eval).</summary>
        public static string Run()
        {
            // Guard: the spray's particle systems only exist in Play mode, and we must never change
            // anything in the Edit-mode scene (it would be saved).
            if (!Application.isPlaying) return "[E2] ABORT: enter Play mode first.";
            var spray = Object.FindAnyObjectByType<SprayEmitter>();
            if (spray == null) return "[E2] ABORT: no SprayEmitter in the scene.";

            var csv = new StringBuilder("layer,launch_speed_mps,dt_s,t_s,y_m,vy_mps\n");
            var summary = new StringBuilder();
            summary.AppendLine("[E2] Spray step test (one particle straight up from y = 1 m, turbulence off)");
            summary.AppendLine($"     Unity 'Maximum Particle Timestep' = {Time.maximumParticleDeltaTime:0.###} s " +
                               "(Unity splits longer steps into pieces no longer than this)");
            summary.AppendLine("layer  v0 | dt      | apex m  t_apex s | vy@50% vy@75% m/s | died at s");

            foreach (Case c in Cases)
            {
                SprayEmitter.Layer layer = System.Array.Find(spray.layers, l => l.name == c.layer);
                if (layer == null || layer.system == null) return $"[E2] ABORT: layer '{c.layer}' not found.";
                foreach (float dt in Steps) RunOne(layer.system, c, dt, csv, summary);
            }

            // Save the CSV next to the other measurements: <repo>/media/S4.9/
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../media/S4.9"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "e2_spray_step_test.csv");
            File.WriteAllText(path, csv.ToString());
            summary.AppendLine("CSV: " + path);
            return summary.ToString();
        }

        static void RunOne(ParticleSystem source, Case c, float dt, StringBuilder csv, StringBuilder summary)
        {
            // 1. A private copy of the layer, with every module setting of the original.
            GameObject go = Object.Instantiate(source.gameObject);
            go.name = "E2_" + c.layer;
            go.hideFlags = HideFlags.DontSave;                   // never written into the scene
            try
            {
                var ps = go.GetComponent<ParticleSystem>();
                ps.Clear(true);                                  // drop any particles copied from the original
                var main = ps.main; main.simulationSpeed = 1f;   // the original follows our clock (may be paused)
                var noise = ps.noise; noise.enabled = false;     // turbulence is random: isolate gravity + drag
                go.GetComponent<ParticleSystemRenderer>().enabled = false;

                // 2. One particle, straight up, fixed speed / lifetime / size.
                ps.Play(true);
                var emit = new ParticleSystem.EmitParams
                {
                    position = new Vector3(0f, LaunchHeight, 0f),
                    velocity = Vector3.up * c.speed,
                    startLifetime = c.lifetime,
                    startSize = 1f,
                };
                ps.Emit(emit, 1);

                // 3. Step it by hand. Simulate(dt, withChildren, restart: false, fixedTimeStep: false)
                //    advances by exactly dt and then leaves the system paused, so the Editor's own
                //    frames don't move it in between.
                var buffer = new ParticleSystem.Particle[1];
                int steps = Mathf.RoundToInt(c.lifetime / dt);
                int sampleStride = Mathf.RoundToInt(SampleEvery / dt);
                float y = LaunchHeight, apex = LaunchHeight, tApex = 0f;
                float vy50 = float.NaN, vy75 = float.NaN, died = float.NaN;
                csv.AppendLine(Row(c, dt, 0f, y, c.speed));

                for (int i = 1; i <= steps; i++)
                {
                    ps.Simulate(dt, true, false, false);
                    float t = i * dt;
                    if (ps.GetParticles(buffer) == 0) { died = t; break; }   // hit the sea plane (or expired)

                    float yNew = buffer[0].position.y;
                    float vy = (yNew - y) / dt;                  // from positions: includes every module's effect
                    y = yNew;

                    if (y > apex) { apex = y; tApex = t; }
                    if (float.IsNaN(vy50) && t >= 0.50f * c.lifetime) vy50 = vy;
                    if (float.IsNaN(vy75) && t >= 0.75f * c.lifetime) vy75 = vy;
                    if (i % sampleStride == 0) csv.AppendLine(Row(c, dt, t, y, vy));
                }

                summary.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-6}{1,3:0} | 1/{2,-5:0} | {3,6:0.00}  {4,6:0.00}   | {5,6:0.00} {6,6:0.00}     | {7}",
                    c.layer, c.speed, 1f / dt, apex, tApex, vy50, vy75,
                    float.IsNaN(died) ? "-" : died.ToString("0.00", CultureInfo.InvariantCulture)));
            }
            finally
            {
                Object.Destroy(go);                              // Play mode: Destroy, not DestroyImmediate
            }
        }

        static string Row(Case c, float dt, float t, float y, float vy) =>
            string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:0.######},{3:0.###},{4:0.####},{5:0.####}",
                          c.layer, c.speed, dt, t, y, vy);
    }
}
