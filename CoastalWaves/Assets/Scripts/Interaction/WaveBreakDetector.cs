using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Coastal
{
    /// <summary>What a break event tells its listeners: "a wave hit the rock here, this high, now".</summary>
    public struct WaveBreakEvent
    {
        public Vector3 position;    // the probe's position, at the crest's peak height
        public Vector3 outward;     // which way the rock faces there (out to sea)
        public float peakHeight;    // how high the crest got, metres above sea level
        public float time;          // simulation time of the peak
        public int probe;           // index of the probe that fired
        public float intensity;     // 0..1: how hard the hit is (height above threshold x how head-on)
        public Vector3 waterVelocity; // water particle velocity at the crest (m/s), e.g. for spray direction
    }

    /// <summary>
    /// Watches the water height right next to the cliff and decides when a wave breaks.
    ///
    /// S2.2: PROBES. Horizontal rays from the sea towards the cliff (+X) at sea level, every
    /// 'spacing' metres along the coast; where a ray hits, a probe is placed a little in front
    /// of the rock. Each step every probe reads the water height (WaveField.HeightAt).
    ///
    /// S2.3: DETECTION (minimal). Each probe runs a 3-state machine:
    ///   Idle     -> Rising   when h >= threshold (and >= minInterval since its last event)
    ///   Rising   -> fire     on the first step h goes DOWN: the crest just peaked -> event
    ///   Cooldown -> Idle     when h < threshold - hysteresis (so a wobbling crest can't fire twice)
    /// Listeners subscribe to the WaveBreak event (foam in S3, spray later).
    ///
    /// S2.4: INFLOW TEST + INTENSITY. Gerstner waves are an analytic sum, so they pass straight
    /// through the rock: a probe on a flank facing away from the swell still sees tall crests.
    /// At the peak we therefore look at which way the water moves. At a Gerstner crest a particle
    /// moves forwards with the wave, so
    ///   exposure = dot(horizontal water direction, into the rock)   (1 head-on, 0 sliding past)
    /// A crest with exposure below minExposure is rejected (counted, but no event).
    /// A crest that passes gets an intensity in [minIntensity, 1] from its height above the
    /// threshold, scaled by sqrt(exposure) so glancing hits are weaker.
    /// </summary>
    public class WaveBreakDetector : MonoBehaviour
    {
        public enum ProbeState { Idle, Rising, Cooldown }

        /// <summary>One measuring point on the waterline.</summary>
        public class Probe
        {
            public Vector3 position;   // fixed point in front of the rock, at sea level
            public Vector3 outward;    // horizontal direction pointing away from the rock, out to sea
            public float height;       // current water height above sea level (metres)
            public float maxHeight;    // highest water seen so far (for debugging)
            public ProbeState state;
            public float peak;         // highest height in the current Rising phase
            public float lastEventTime = -999f;
            public int eventCount;
            public int rejectCount;     // crests that peaked above threshold but moved away from / along the rock
            public float lastIntensity; // intensity of this probe's latest event
        }

        [Tooltip("The ocean to sample.")]
        public WaveField waves;
        [Tooltip("The cliff's collider (only used to find the waterline).")]
        public Collider cliff;

        [Header("Probe placement")]
        // S4.10b: the whole coast (the cliff runs z -200..200 m); 159 probes at 2.5 m.
        public float zMin = -197.5f, zMax = 197.5f;
        [Tooltip("Distance between neighbouring probes along the coast (metres).")]
        public float spacing = 2.5f;
        [Tooltip("How far in front of the rock surface the water is sampled (metres).")]
        public float offset = 0.6f;

        [Header("Break detection (S2.3)")]
        [Tooltip("Water height above sea level (metres) that counts as a wave breaking on the rock.")]
        public float threshold = 2f;   // S4.7: was 3 m; every collision should throw spray, scaled by intensity
        [Tooltip("The water must fall this far BELOW the threshold before the probe can fire again.")]
        public float hysteresis = 0.25f;
        [Tooltip("Minimum time between two events from the same probe (seconds).")]
        public float minInterval = 1f;

        [Header("Inflow test and intensity (S2.4)")]
        [Tooltip("Only count crests whose water moves INTO the rock (Gerstner waves pass through rock, so the lee side also sees crests).")]
        public bool requireInflow = true;
        [Tooltip("Smallest exposure that still counts: 1 = water moving straight into the rock, 0 = sliding along it.")]
        [Range(0f, 1f)] public float minExposure = 0.15f;
        [Tooltip("Intensity of a crest that only just reaches the threshold.")]
        [Range(0f, 1f)] public float minIntensity = 0.2f;
        [Tooltip("Height above the threshold (metres) that gives full intensity 1. " +
                 "3 m: with reflection the tallest crests at the cliff reach about 5 m, 3 m above the 2 m threshold " +
                 "(S4.7; was 0.9 m for a 3 m threshold).")]
        public float intensityRange = 3f;

        /// <summary>Raised once per crest that breaks on the rock. Subscribe with detector.WaveBreak += ...</summary>
        public event Action<WaveBreakEvent> WaveBreak;

        [Header("Debug")]
        public bool debugLogs = true;
        [Tooltip("Write one Console line per event (can be chatty).")]
        public bool logEachEvent = false;
        [Tooltip("Record every probe's height (every 0.05 s of sim time) so a trace can be saved as CSV.")]
        public bool recordTrace = false;
        const string Tag = "[Detector] ";

        public List<Probe> Probes { get; } = new List<Probe>();
        public int TotalEvents { get; private set; }
        public int RejectedEvents { get; private set; }   // crests stopped by the inflow test
        public float LastTickMs { get; private set; }

        // Trace recording (debug/verification only).
        readonly List<float> traceTimes = new List<float>();
        readonly List<float[]> traceHeights = new List<float[]>();
        readonly List<WaveBreakEvent> recentEvents = new List<WaveBreakEvent>();
        float nextTraceTime;

        // Build in Start, not OnEnable: when Play begins, Unity calls every object's OnEnable in
        // no guaranteed order, and the cliff builds its mesh + collider in ITS OnEnable. If we
        // raycast first, the collider is still empty and we get 0 probes (bug found in S2.2).
        // Start runs after all OnEnable calls of the scene, so the collider is ready by then.
        void Start() => BuildProbes();

        /// <summary>Find the waterline along the cliff and put a probe in front of it every 'spacing' m.</summary>
        [ContextMenu("Rebuild Probes")]
        public void BuildProbes()
        {
            Probes.Clear();
            ResetDetection();
            if (cliff == null || waves == null) return;

            int misses = 0;
            float sea = waves.seaLevel;
            for (float z = zMin; z <= zMax + 1e-3f; z += spacing)
            {
                var ray = new Ray(new Vector3(-100f, sea, z), Vector3.right);   // from far out at sea, towards the land
                if (!cliff.Raycast(ray, out RaycastHit hit, 400f)) { misses++; continue; }

                Vector3 outward = Vector3.ProjectOnPlane(hit.normal, Vector3.up);  // rock normal, flattened
                outward = outward.sqrMagnitude > 1e-4f ? outward.normalized : Vector3.left;
                Probes.Add(new Probe { position = hit.point + outward * offset, outward = outward });
            }

            if (debugLogs)
                Debug.Log(Tag + $"{Probes.Count} probes along z {zMin}..{zMax} m every {spacing} m " +
                          $"({misses} rays missed the cliff)", this);
        }

        /// <summary>Forget all states, counters and recordings (e.g. when the clock restarts).</summary>
        public void ResetDetection()
        {
            TotalEvents = 0;
            RejectedEvents = 0;
            foreach (Probe p in Probes)
            {
                p.state = ProbeState.Idle; p.peak = 0f; p.lastEventTime = -999f;
                p.eventCount = 0; p.rejectCount = 0; p.lastIntensity = 0f;
            }
            traceTimes.Clear(); traceHeights.Clear(); recentEvents.Clear(); nextTraceTime = 0f;
        }

        /// <summary>Called by the clock every step: read the water at every probe and run the state machine.</summary>
        public void Tick(float t)
        {
            if (waves == null) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            float sea = waves.seaLevel;

            for (int i = 0; i < Probes.Count; i++)
            {
                Probe p = Probes[i];
                p.height = waves.HeightAt(p.position.x, p.position.z, t) - sea;
                p.maxHeight = Mathf.Max(p.maxHeight, p.height);

                switch (p.state)
                {
                    case ProbeState.Idle:
                        // Crest arriving above the threshold (and this probe has rested long enough).
                        if (p.height >= threshold && t - p.lastEventTime >= minInterval)
                        {
                            p.state = ProbeState.Rising;
                            p.peak = p.height;
                        }
                        break;

                    case ProbeState.Rising:
                        if (p.height > p.peak)
                        {
                            p.peak = p.height;          // still rising: remember the highest point
                        }
                        else
                        {
                            Fire(i, p, t);              // first step going down: the crest just peaked
                            p.state = ProbeState.Cooldown;   // this crest is used up, fired or rejected
                        }
                        break;

                    case ProbeState.Cooldown:
                        // Re-arm only once the water is clearly below the threshold (hysteresis).
                        if (p.height < threshold - hysteresis) p.state = ProbeState.Idle;
                        break;
                }
            }

            if (recordTrace && t >= nextTraceTime) RecordTraceSample(t);

            timer.Stop();
            LastTickMs = (float)timer.Elapsed.TotalMilliseconds;
        }

        /// <summary>A crest just peaked at probe p: check the water moves into the rock, then raise the event.</summary>
        void Fire(int index, Probe p, float t)
        {
            // Intensity from height: just over the threshold -> minIntensity, intensityRange above -> 1.
            float excess = p.peak - threshold;
            float intensity = Mathf.Lerp(minIntensity, 1f, Mathf.Clamp01(excess / intensityRange));

            // Which way is the water moving right now? (SampleAt finds the particle that is AT the probe.)
            Vector3 velocity = waves.SampleAt(p.position.x, p.position.z, t).velocity;

            float exposure = 1f;
            if (requireInflow)
            {
                // Horizontal flow direction vs. the direction into the rock (-outward).
                var flow = new Vector3(velocity.x, 0f, velocity.z);
                exposure = flow.sqrMagnitude > 1e-6f ? Vector3.Dot(flow.normalized, -p.outward) : 0f;
                if (exposure < minExposure)
                {
                    // Tall water, but it slides past or moves away from the rock: no impact.
                    // lastEventTime is NOT updated, since nothing hit the rock.
                    p.rejectCount++;
                    RejectedEvents++;
                    return;
                }
                intensity *= Mathf.Sqrt(exposure);   // glancing hits are weaker (exposure 0.25 -> x0.5)
            }

            var e = new WaveBreakEvent
            {
                position = new Vector3(p.position.x, waves.seaLevel + p.peak, p.position.z),
                outward = p.outward,
                peakHeight = p.peak,
                time = t,
                probe = index,
                intensity = intensity,
                waterVelocity = velocity,
            };
            p.lastEventTime = t;
            p.lastIntensity = intensity;
            p.eventCount++;
            TotalEvents++;
            if (recordTrace) recentEvents.Add(e);
            if (debugLogs && logEachEvent)
                Debug.Log(Tag + $"break at probe {index} (z = {p.position.z:F1}) t = {t:F2} s, peak {p.peak:F2} m, " +
                          $"exposure {exposure:F2}, intensity {intensity:F2}", this);

            WaveBreak?.Invoke(e);   // tell every subscriber (none yet: foam comes in S3)
        }

        // ---------------------------------------------------------------
        // Verification: record heights, save one probe's trace with its events as CSV
        // ---------------------------------------------------------------

        void RecordTraceSample(float t)
        {
            var heights = new float[Probes.Count];
            for (int i = 0; i < Probes.Count; i++) heights[i] = Probes[i].height;
            traceTimes.Add(t);
            traceHeights.Add(heights);
            nextTraceTime = t + 0.05f;
        }

        /// <summary>Index of the probe with the most events so far.</summary>
        public int BusiestProbe()
        {
            int best = 0;
            for (int i = 1; i < Probes.Count; i++) if (Probes[i].eventCount > Probes[best].eventCount) best = i;
            return best;
        }

        /// <summary>
        /// Write one probe's recorded height trace and its events to a CSV
        /// (columns: time, height, event_peak or empty). Returns the file path.
        /// </summary>
        public string SaveTraceCsv(int probe, string path)
        {
            var events = new Dictionary<int, float>();   // sample index nearest to each event -> peak
            foreach (WaveBreakEvent e in recentEvents)
            {
                if (e.probe != probe) continue;
                int nearest = 0;
                for (int k = 1; k < traceTimes.Count; k++)
                    if (Mathf.Abs(traceTimes[k] - e.time) < Mathf.Abs(traceTimes[nearest] - e.time)) nearest = k;
                events[nearest] = e.peakHeight;
            }

            var csv = new StringBuilder("time_s,height_m,event_peak_m\n");
            for (int k = 0; k < traceTimes.Count; k++)
            {
                csv.Append(traceTimes[k].ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                   .Append(traceHeights[k][probe].ToString("F4", CultureInfo.InvariantCulture)).Append(',');
                if (events.TryGetValue(k, out float peak)) csv.Append(peak.ToString("F4", CultureInfo.InvariantCulture));
                csv.Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, csv.ToString());
            return path;
        }

        // ---------------------------------------------------------------
        // Scene-view drawing: probe colour by state
        // ---------------------------------------------------------------

        void OnDrawGizmos()
        {
            if (waves == null) return;
            foreach (Probe p in Probes)
            {
                Vector3 water = new Vector3(p.position.x, waves.seaLevel + p.height, p.position.z);
                Gizmos.color = p.state == ProbeState.Rising ? Color.yellow
                             : p.state == ProbeState.Cooldown ? Color.red : Color.cyan;
                Gizmos.DrawLine(p.position, water);
                Gizmos.DrawSphere(water, p.state == ProbeState.Idle ? 0.25f : 0.45f);
                Gizmos.color = Color.white;
                Gizmos.DrawLine(p.position, p.position + p.outward * 1.5f);
            }
        }
    }
}
