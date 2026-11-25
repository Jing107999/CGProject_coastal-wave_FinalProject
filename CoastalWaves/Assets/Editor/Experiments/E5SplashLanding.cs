// EXPERIMENT E5: is the predicted landing point of a spray droplet where Unity's droplet really lands?
//
// Menu: Coastal > Experiments > E5 Splash Landing   (needs PLAY mode, runs in well under 1 s)
//
// Why: S4.11 lays a splash-back foam patch where the farthest droplet of a burst falls back into the
// sea. It doesn't watch the particles; it PREDICTS the landing point with the ballistic formula
// (SprayEmitter.PredictLanding). That is only right if Unity moves the droplets the same way.
//
// Protocol (fixed before running):
//  - Copy the live Droplets layer (its exact module settings, incl. the sea-level kill plane),
//    switch off its renderer.
//  - Launch ONE droplet per case from a fixed point 3 m above the sea, with a fixed velocity and a
//    long lifetime (so it lands rather than expires).
//  - Step it by hand with ParticleSystem.Simulate(dt) (as in E2) at dt = 1/60 and 1/240 s, until it
//    disappears (killed by the sea plane). Its landing point = the last position, carried along the
//    last velocity down to the sea level (it disappears within one step of crossing it).
//  - Compare with the prediction: horizontal error (m) and landing-time error (s).
//  - Cases: speeds 4, 8, 12 m/s at 30, 55, 75 degrees above horizontal (the droplets' range).
//  - Rule: every horizontal error < 0.1 m.
// Output: a summary in the Console ("[E5]").

using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Coastal.EditorTools
{
    public static class E5SplashLanding
    {
        static readonly float[] Speeds = { 4f, 8f, 12f };
        static readonly float[] Angles = { 30f, 55f, 75f };   // degrees above horizontal
        static readonly float[] Steps = { 1f / 60f, 1f / 240f };
        const float LaunchHeight = 3f;                        // m above the sea
        const float Rule = 0.1f;                              // m

        [MenuItem("Coastal/Experiments/E5 Splash Landing")]
        public static void RunFromMenu() => Debug.Log(Run());

        public static string Run()
        {
            if (!Application.isPlaying) return "[E5] ABORT: enter Play mode first.";
            var spray = Object.FindAnyObjectByType<SprayEmitter>();
            if (spray == null) return "[E5] ABORT: no SprayEmitter.";
            SprayEmitter.Layer layer = System.Array.Find(spray.layers, l => l.name == "Droplets");
            if (layer == null || layer.system == null) return "[E5] ABORT: no Droplets layer.";

            float g = -Physics.gravity.y * layer.gravity;
            var s = new StringBuilder("[E5] Splash landing: predicted vs Unity (launch 3 m above the sea)\n");
            s.AppendLine("v m/s  angle | dt     | predicted x m  t s | unity x m  t s | error m   dt s");
            float worst = 0f;
            foreach (float dt in Steps)
                foreach (float speed in Speeds)
                    foreach (float angle in Angles)
                    {
                        float a = angle * Mathf.Deg2Rad;
                        // Launch straight along +x (out to sea direction doesn't matter for the test).
                        var p = new Vector3(0f, spray.seaLevel + LaunchHeight, 0f);
                        var v = new Vector3(speed * Mathf.Cos(a), speed * Mathf.Sin(a), 0f);
                        SprayEmitter.PredictLanding(p, v, g, spray.seaLevel, out Vector2 predicted, out float tPredicted);

                        Vector3 last; float tUnity;
                        if (!Fly(layer.system, p, v, dt, spray.seaLevel, out last, out tUnity))
                        { s.AppendLine($"{speed,5:0} {angle,5:0} | droplet never landed (ABORT)"); continue; }

                        float error = Vector2.Distance(predicted, new Vector2(last.x, last.z));
                        worst = Mathf.Max(worst, error);
                        s.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "{0,5:0} {1,5:0} | 1/{2,-4:0} | {3,8:0.000} {4,6:0.000} | {5,8:0.000} {6,6:0.000} | {7,7:0.0000} {8,6:0.000}",
                            speed, angle, 1f / dt, predicted.x, tPredicted, last.x, tUnity, error, tUnity - tPredicted));
                    }
            s.AppendLine(string.Format(CultureInfo.InvariantCulture, "Worst horizontal error {0:0.0000} m (rule < {1} m): {2}",
                                       worst, Rule, worst < Rule ? "PASS" : "FAIL"));
            return s.ToString();
        }

        // Flies one droplet in a private copy of the layer; returns where it crossed the sea level.
        static bool Fly(ParticleSystem source, Vector3 p, Vector3 v, float dt, float seaLevel, out Vector3 landing, out float time)
        {
            landing = default; time = 0f;
            GameObject go = Object.Instantiate(source.gameObject);
            go.hideFlags = HideFlags.DontSave;                      // never written into the scene
            try
            {
                var ps = go.GetComponent<ParticleSystem>();
                ps.Clear(true);
                var main = ps.main; main.simulationSpeed = 1f;       // the original follows our (maybe paused) clock
                go.GetComponent<ParticleSystemRenderer>().enabled = false;
                ps.Play(true);
                ps.Emit(new ParticleSystem.EmitParams { position = p, velocity = v, startLifetime = 30f, startSize = 0.2f }, 1);

                var buffer = new ParticleSystem.Particle[1];
                Vector3 lastPos = p, lastVel = v;
                for (int i = 1; i <= 30f / dt; i++)
                {
                    ps.Simulate(dt, true, false, false);
                    if (ps.GetParticles(buffer) == 0)
                    {
                        // Gone: it crossed the sea plane during this step. Carry the last position along the
                        // last velocity down to the sea level to get the crossing point and time.
                        float down = (lastPos.y - seaLevel) / Mathf.Max(1e-4f, -lastVel.y);
                        landing = lastPos + lastVel * down;
                        time = (i - 1) * dt + down;
                        return true;
                    }
                    lastPos = buffer[0].position;
                    lastVel = buffer[0].totalVelocity;
                }
                return false;
            }
            finally { Object.Destroy(go); }
        }
    }
}
