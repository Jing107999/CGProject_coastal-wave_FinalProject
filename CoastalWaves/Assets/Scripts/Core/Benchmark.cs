using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// Frame-time benchmark for the built player (S5.6). Does nothing unless the player is
    /// started with "-benchmark". Then, for each water-grid resolution, it restarts the
    /// simulation, warms up, records every frame's time and the detector's CPU time, writes
    /// two CSV files (summary and per-frame) and quits.
    ///
    /// Run (macOS):  CoastalWaves.app/Contents/MacOS/CoastalWaves -benchmark
    ///               -screen-width 1920 -screen-height 1080 -screen-fullscreen 0
    ///               [-benchmarkOut /path/to/folder] [-benchmarkWarmup 15] [-benchmarkSeconds 30]
    /// vsync is switched off and the frame rate uncapped while it runs. The HUD and the foam
    /// preview are hidden, so their drawing cost is not included.
    /// </summary>
    public class Benchmark : MonoBehaviour
    {
        static readonly int[] Resolutions = { 128, 256, 512, 1024 };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-benchmark") < 0) return;
            DontDestroyOnLoad(new GameObject("Benchmark").AddComponent<Benchmark>().gameObject);
        }

        static string Arg(string name, string fallback)
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        IEnumerator Start()
        {
            var inv = CultureInfo.InvariantCulture;
            float warmup = float.Parse(Arg("-benchmarkWarmup", "15"), inv);
            float seconds = float.Parse(Arg("-benchmarkSeconds", "30"), inv);
            string outDir = Arg("-benchmarkOut", Application.persistentDataPath);
            Directory.CreateDirectory(outDir);

            Application.runInBackground = true;   // keep running when the window loses focus
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            var clock = FindAnyObjectByType<CoastalSimulation>();
            var water = FindAnyObjectByType<WaterSurface>();
            var detector = FindAnyObjectByType<WaveBreakDetector>();
            var controls = FindAnyObjectByType<CoastalControls>();
            var foam = FindAnyObjectByType<FoamSystem>();
            var orbit = FindAnyObjectByType<OrbitCamera>();
            if (clock == null || water == null) { Debug.LogError("[Benchmark] scene not found"); Application.Quit(1); yield break; }

            // Same state as the report renders: splash-back foam (S4.11) off, overlays hidden,
            // demo camera pose.
            foreach (var s in FindObjectsByType<SprayEmitter>(FindObjectsSortMode.None)) s.splashBackFoam = false;
            if (controls != null) controls.showOverlay = false;
            if (foam != null) foam.showPreview = false;
            if (orbit != null) { orbit.target = new Vector3(2f, 0f, -12f); orbit.yaw = 56.3f; orbit.pitch = 16f; orbit.distance = 60f; }

            var summary = new StringBuilder("resolution,vertices,frames,mean_ms,p50_ms,p95_ms,p99_ms,max_ms,fps_mean,detector_mean_ms,events\n");
            var frames = new StringBuilder("resolution,frame,dt_ms,detector_ms\n");
            int startRes = water.resolution;

            foreach (int res in Resolutions)
            {
                water.resolution = res;
                water.Build();
                clock.paused = false;
                clock.timeScale = 1f;
                clock.Restart();

                float t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < warmup) yield return null;

                var dts = new List<float>();
                double detSum = 0;
                int events0 = detector != null ? detector.TotalEvents : 0;
                t0 = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - t0 < seconds)
                {
                    yield return null;
                    float ms = Time.unscaledDeltaTime * 1000f;
                    float det = detector != null ? detector.LastTickMs : 0f;
                    dts.Add(ms); detSum += det;
                    frames.Append(res).Append(',').Append(dts.Count).Append(',')
                          .Append(ms.ToString("F3", inv)).Append(',').Append(det.ToString("F4", inv)).Append('\n');
                }

                var sorted = new List<float>(dts); sorted.Sort();
                double mean = 0; foreach (float d in dts) mean += d; mean /= dts.Count;
                float P(float q) => sorted[Mathf.Clamp(Mathf.CeilToInt(q * sorted.Count) - 1, 0, sorted.Count - 1)];
                int events = detector != null ? detector.TotalEvents - events0 : 0;
                summary.Append(string.Format(inv, "{0},{1},{2},{3:F3},{4:F3},{5:F3},{6:F3},{7:F3},{8:F1},{9:F4},{10}\n",
                    res, water.VertexCount, dts.Count, mean, P(0.5f), P(0.95f), P(0.99f), sorted[sorted.Count - 1],
                    1000.0 / mean, detSum / dts.Count, events));
                Debug.Log($"[Benchmark] {res}^2: mean {mean:F2} ms, p95 {P(0.95f):F2} ms");
            }

            water.resolution = startRes;
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", inv);
            string head = $"# device: {SystemInfo.deviceModel}; cpu: {SystemInfo.processorType} ({SystemInfo.processorCount} cores); " +
                          $"gpu: {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsDeviceType}); screen {Screen.width}x{Screen.height}; " +
                          $"warmup {warmup} s, measured {seconds} s per resolution; vsync off\n";
            File.WriteAllText(Path.Combine(outDir, $"benchmark_summary_{stamp}.csv"), head + summary);
            File.WriteAllText(Path.Combine(outDir, $"benchmark_frames_{stamp}.csv"), head + frames);
            Debug.Log("[Benchmark] done, written to " + outDir);
            Application.Quit();
        }
    }
}
