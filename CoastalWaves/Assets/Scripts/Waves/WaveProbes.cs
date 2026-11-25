using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// DEBUG / VERIFICATION TOOL: a row of "buoys" that ride the CPU wave surface.
    ///
    /// WHY: before any shader exists, we want to SEE and MEASURE that the wave maths
    /// is right. If the GPU water looks wrong later, we'll know the CPU half is fine.
    ///
    /// WHAT:
    ///  - Every child object of this GameObject is a buoy. Buoy i rests at
    ///    lineStart + i * spacing along lineDirectionDeg, and each frame is moved to
    ///    WaveField.Evaluate(rest point, t).position.
    ///  - Scene-view Gizmos: the wave PROFILE (a cut through the surface along the
    ///    buoy line, cyan), each buoy's recent TRAIL (yellow; a circle for a single wave),
    ///    and the REST points (small white dots).
    ///  - Measurements from buoy 0, logged every few seconds: orbit radius, period,
    ///    and how fast the crest travels. For a single wave, the expected values from
    ///    the formulas are logged next to them for comparison.
    /// </summary>
    [ExecuteAlways]
    public class WaveProbes : MonoBehaviour
    {
        [Tooltip("The wave field to sample.")]
        public WaveField waveField;

        [Header("Buoy line")]
        [Tooltip("Rest position of the first buoy (y is ignored; buoys rest at sea level).")]
        public Vector3 lineStart = new Vector3(-10f, 0f, 0f);

        [Tooltip("Direction of the buoy line in degrees (0 = along +X, the main wave direction).")]
        public float lineDirectionDeg = 0f;

        [Tooltip("Distance between neighbouring buoys at rest (metres).")]
        [Min(0.1f)] public float spacing = 2.5f;

        [Header("Gizmos (Scene view)")]
        [Tooltip("How far the drawn profile extends on each side of the buoy line (metres).")]
        [Min(1f)] public float profileExtent = 30f;

        [Tooltip("Number of points used to draw the profile line.")]
        [Range(16, 1024)] public int profileSamples = 300;

        [Tooltip("Number of past positions kept per buoy for its trail.")]
        [Range(2, 600)] public int trailLength = 240;

        [Tooltip("Simulation seconds between two stored trail points. The trail then covers " +
                 "trailLength x this many seconds (240 x 0.02 = 4.8 s, more than one orbit), " +
                 "no matter how fast the frames come.")]
        [Min(0.001f)] public float trailSampleInterval = 0.02f;

        [Header("Measurement")]
        [Tooltip("Seconds of simulation time between measurement log lines.")]
        [Min(0.5f)] public float logInterval = 5f;

        public bool debugLogs = true;

        const string Tag = "[Probes] ";

        // Trails: a ring buffer of past positions for each buoy.
        Vector3[][] trails;
        int trailHead, trailCount;
        float lastTrailTime = float.NegativeInfinity; // sim time of the newest stored trail point

        // Measurement state (buoy 0 and the crest).
        float lastT = -1f;
        float minY, maxY;
        float lastCrossTime = -1f, periodSum; int periodCount;
        float prevY;
        float lastCrestX = float.NaN, crestDistance, crestTime;
        float nextLogTime;
        float watchedSteepness = -1f; int watchedCount = -1;

        // ---------------------------------------------------------------
        // Unity callbacks
        // ---------------------------------------------------------------

        void OnEnable() => ResetMeasurements();

        // LateUpdate runs after every Update(), so the clock has already advanced the time.
        void LateUpdate()
        {
            if (waveField == null) return;

            // Wave settings changed (e.g. switched to the test wave)? Start measuring afresh.
            if (!Mathf.Approximately(watchedSteepness, waveField.TotalSteepness) ||
                watchedCount != waveField.Components.Length)
                ResetMeasurements();

            float t = waveField.SimTime;
            MoveBuoys(t);

            // Only measure while time is actually running.
            if (Application.isPlaying && t > lastT && lastT >= 0f) Measure(t, t - lastT);
            lastT = t;
        }

        // ---------------------------------------------------------------
        // Buoys
        // ---------------------------------------------------------------

        Vector3 LineDirection()
        {
            float rad = lineDirectionDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad));
        }

        Vector3 RestPoint(int i) => new Vector3(lineStart.x, 0f, lineStart.z) + LineDirection() * (spacing * i);

        void MoveBuoys(float t)
        {
            int n = transform.childCount;
            if (trails == null || trails.Length != n || trails[0].Length != trailLength)
            {
                trails = new Vector3[n][];
                for (int i = 0; i < n; i++) trails[i] = new Vector3[trailLength];
                trailHead = trailCount = 0;
            }

            // Time went backwards (clock restarted)? Start the trails afresh.
            if (t < lastTrailTime) { trailHead = trailCount = 0; lastTrailTime = float.NegativeInfinity; }

            // Store a trail point only every trailSampleInterval seconds of SIMULATION time,
            // not every frame. Otherwise the trail's duration would depend on the frame rate
            // (at 430 fps, 240 frames is only 0.55 s, a short arc instead of a full orbit).
            bool storeTrail = t - lastTrailTime >= trailSampleInterval;

            for (int i = 0; i < n; i++)
            {
                Vector3 rest = RestPoint(i);
                Vector3 p = waveField.Evaluate(rest.x, rest.z, t).position;
                transform.GetChild(i).position = p;
                if (storeTrail) trails[i][trailHead] = p;
            }

            if (storeTrail)
            {
                trailHead = (trailHead + 1) % trailLength;
                trailCount = Mathf.Min(trailCount + 1, trailLength);
                lastTrailTime = t;
            }
        }

        // ---------------------------------------------------------------
        // Measurements
        // ---------------------------------------------------------------

        [ContextMenu("Reset Measurements")]
        public void ResetMeasurements()
        {
            minY = float.MaxValue; maxY = float.MinValue;
            lastCrossTime = -1f; periodSum = 0f; periodCount = 0;
            prevY = 0f;
            lastCrestX = float.NaN; crestDistance = 0f; crestTime = 0f;
            trailHead = trailCount = 0;
            lastTrailTime = float.NegativeInfinity;
            lastT = -1f;
            nextLogTime = (waveField != null ? waveField.SimTime : 0f) + logInterval;
            if (waveField != null)
            {
                watchedSteepness = waveField.TotalSteepness;
                watchedCount = waveField.Components.Length;
            }
        }

        void Measure(float t, float dt)
        {
            Vector3 rest0 = RestPoint(0);
            float y = waveField.Evaluate(rest0.x, rest0.z, t).position.y - waveField.seaLevel;

            // 1. ORBIT RADIUS: half the distance between the highest and lowest point seen.
            minY = Mathf.Min(minY, y);
            maxY = Mathf.Max(maxY, y);

            // 2. PERIOD: time between "upward zero crossings" (y goes from below to above sea level).
            if (prevY < 0f && y >= 0f)
            {
                // Linear interpolation for the exact moment y was 0 (more precise than the frame time).
                float crossTime = t - dt * (y / (y - prevY));
                if (lastCrossTime >= 0f) { periodSum += crossTime - lastCrossTime; periodCount++; }
                lastCrossTime = crossTime;
            }
            prevY = y;

            // 3. CREST SPEED: find the highest point of the profile and follow it.
            float crestX = FindCrestAlongLine(t);
            if (!float.IsNaN(lastCrestX))
            {
                float dx = crestX - lastCrestX;
                // A crest leaving the window makes the next one "jump" backwards. Ignore those frames.
                if (dx > 0f && dx < spacing * 4f) { crestDistance += dx; crestTime += dt; }
            }
            lastCrestX = crestX;

            if (debugLogs && t >= nextLogTime)
            {
                LogMeasurements();
                nextLogTime += logInterval;
            }
        }

        /// <summary>Distance along the buoy line of the highest surface point near the buoys.</summary>
        float FindCrestAlongLine(float t)
        {
            Vector3 dir = LineDirection();
            Vector3 origin = RestPoint(0);
            float length = spacing * Mathf.Max(1, transform.childCount - 1);
            const int samples = 200;

            int best = 0; float bestY = float.MinValue;
            var ys = new float[samples + 1];
            var along = new float[samples + 1];
            for (int j = 0; j <= samples; j++)
            {
                Vector3 rest = origin + dir * (length * j / samples);
                Vector3 p = waveField.Evaluate(rest.x, rest.z, t).position;
                ys[j] = p.y;
                along[j] = Vector3.Dot(p - origin, dir); // displaced position along the line
                if (p.y > bestY) { bestY = p.y; best = j; }
            }

            // Refine with a parabola through the best sample and its neighbours,
            // so the crest position isn't limited to the sample spacing.
            if (best > 0 && best < samples)
            {
                float y0 = ys[best - 1], y1 = ys[best], y2 = ys[best + 1];
                float denom = y0 - 2f * y1 + y2;
                float offset = Mathf.Abs(denom) > 1e-6f ? 0.5f * (y0 - y2) / denom : 0f; // -0.5..0.5 samples
                return Mathf.Lerp(along[best], offset > 0 ? along[best + 1] : along[best - 1], Mathf.Abs(offset));
            }
            return along[best];
        }

        void LogMeasurements()
        {
            float radius = (maxY - minY) * 0.5f;
            string period = periodCount > 0 ? $"{periodSum / periodCount:F2} s" : "n/a";
            string speed = crestTime > 0f ? $"{crestDistance / crestTime:F2} m/s" : "n/a";
            string line = Tag + $"buoy 0: orbit radius = {radius:F2} m, period = {period}, crest speed = {speed}";

            // For a single wave the formulas predict exact values. Show them side by side.
            if (waveField.Components.Length == 1)
            {
                var c = waveField.Components[0];
                line += $"  |  expected: radius = {c.a:F2} m, period = {2f * Mathf.PI / c.omega:F2} s, " +
                        $"crest speed = {c.omega / c.k:F2} m/s";
            }
            else
            {
                line += $"  ({waveField.Components.Length} waves: no single expected value; " +
                        $"height range {minY:F2} .. {maxY:F2} m, max possible {waveField.MaxHeight:F2} m)";
            }
            Debug.Log(line, this);
        }

        // ---------------------------------------------------------------
        // Scene-view drawing
        // ---------------------------------------------------------------

        void OnDrawGizmos()
        {
            if (waveField == null) return;
            float t = waveField.SimTime;
            Vector3 dir = LineDirection();
            Vector3 mid = RestPoint(0) + dir * (spacing * Mathf.Max(0, transform.childCount - 1) * 0.5f);

            // Wave profile: cut through the surface along the buoy line.
            Gizmos.color = Color.cyan;
            Vector3 prev = Vector3.zero;
            for (int j = 0; j <= profileSamples; j++)
            {
                float u = Mathf.Lerp(-profileExtent, profileExtent, j / (float)profileSamples);
                Vector3 rest = mid + dir * u;
                Vector3 p = waveField.Evaluate(rest.x, rest.z, t).position;
                if (j > 0) Gizmos.DrawLine(prev, p);
                prev = p;
            }

            // Rest points (where each buoy would be on a calm sea).
            Gizmos.color = Color.white;
            for (int i = 0; i < transform.childCount; i++) Gizmos.DrawSphere(RestPoint(i), 0.08f);

            // Trails: the path each buoy has followed recently.
            if (trails == null || trailCount < 2) return;
            Gizmos.color = Color.yellow;
            for (int i = 0; i < trails.Length; i++)
            {
                for (int k = 1; k < trailCount; k++)
                {
                    int a = (trailHead - k + trailLength) % trailLength;
                    int b = (trailHead - k - 1 + trailLength) % trailLength;
                    Gizmos.DrawLine(trails[i][a], trails[i][b]);
                }
            }
        }
    }
}
