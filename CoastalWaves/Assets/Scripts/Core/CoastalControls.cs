using UnityEngine;
using UnityEngine.InputSystem;

namespace Coastal
{
    /// <summary>
    /// Keyboard controls for the simulation, plus a small on-screen overlay (HUD).
    ///
    ///   P       pause / resume            Space   restart at t = 0
    ///   7 / 8   slower / faster           5 / 6   calmer / rougher sea (capped, see D21)
    ///   F       cycle view: shaded -> normals -> Jacobian -> foam map
    ///   G       show / hide debug objects H       show / hide this overlay
    ///   1 / 2   lower / raise the wave-break threshold by 0.1 m
    ///   3 / 4   shorter / longer foam lifetime tau (x0.8 / x1.25)
    ///
    /// The overlay uses IMGUI (OnGUI): the simplest way to draw text from code,
    /// ideal for a debug/demo display.
    /// </summary>
    [DisallowMultipleComponent]
    public class CoastalControls : MonoBehaviour
    {
        [Header("What the keys control")]
        public CoastalSimulation simulation;
        public WaveField waves;
        [Tooltip("The wave-break detector; 1/2 change its threshold. Empty = use the simulation's detector.")]
        public WaveBreakDetector detector;
        [Tooltip("The foam map; 3/4 change its lifetime. Empty = use the simulation's foam.")]
        public FoamSystem foam;
        [Tooltip("The water's renderer; F switches its debug view.")]
        public Renderer water;
        [Tooltip("Objects that G shows / hides (probe buoys, height marker, ...).")]
        public GameObject[] debugObjects = new GameObject[0];

        [Header("Limits")]
        [Tooltip("Highest steepness scale allowed from the keyboard. 1.1 keeps total steepness " +
                 "at 0.83, inside the range where 6 inversion iterations were shown accurate " +
                 "(experiment E1, decision D21).")]
        public float maxSteepnessScale = 1.1f;
        public float steepnessStep = 0.05f;
        public Vector2 timeScaleRange = new Vector2(0.1f, 3f);
        public float timeScaleStep = 0.1f;
        [Tooltip("How much one press of 1 / 2 changes the break threshold (metres).")]
        public float thresholdStep = 0.1f;
        [Tooltip("Lowest break threshold allowed from the keyboard (metres).")]
        public float minThreshold = 0.1f;
        [Tooltip("3 multiplies the foam lifetime by this, 4 divides by it. Geometric steps: 4 -> 3.2 -> 2.56 ... and back.")]
        public float lifetimeFactor = 0.8f;
        [Tooltip("Shortest foam lifetime allowed from the keyboard (seconds).")]
        public float minLifetime = 0.25f;

        [Header("Overlay")]
        public bool showOverlay = true;

        static readonly string[] ViewNames = { "Shaded", "Normals", "Jacobian", "Foam map" };
        static readonly int DebugViewId = Shader.PropertyToID("_DebugView");

        int view;                  // 0 = shaded, 1 = normals, 2 = Jacobian, 3 = foam map
        bool debugVisible = true;
        float smoothedFrameTime;   // for a steady fps readout
        MaterialPropertyBlock block;
        GUIStyle boxStyle, labelStyle;
        Texture2D background;

        // Event rate since the threshold was last changed (or the clock restarted):
        // remember the sim time and event count at that moment.
        float rateStartTime;
        int rateStartEvents;

        /// <summary>The detector to control: the one set in the Inspector, else the simulation's.</summary>
        WaveBreakDetector Detector => detector != null ? detector : simulation != null ? simulation.detector : null;
        /// <summary>The foam to control: the one set in the Inspector, else the simulation's.</summary>
        FoamSystem Foam => foam != null ? foam : simulation != null ? simulation.foam : null;

        void Update()
        {
            Keyboard keys = Keyboard.current;
            if (keys == null) return;

            // wasPressedThisFrame = true only on the frame the key goes down -> good for toggles.
            if (keys.pKey.wasPressedThisFrame && simulation != null) simulation.paused = !simulation.paused;
            if (keys.spaceKey.wasPressedThisFrame && simulation != null) simulation.Restart();

            if (keys.digit1Key.wasPressedThisFrame) ChangeThreshold(-thresholdStep);
            if (keys.digit2Key.wasPressedThisFrame) ChangeThreshold(+thresholdStep);
            if (keys.digit3Key.wasPressedThisFrame) ChangeFoamLifetime(lifetimeFactor);
            if (keys.digit4Key.wasPressedThisFrame) ChangeFoamLifetime(1f / lifetimeFactor);

            if (keys.digit7Key.wasPressedThisFrame) ChangeTimeScale(-timeScaleStep);
            if (keys.digit8Key.wasPressedThisFrame) ChangeTimeScale(+timeScaleStep);
            if (keys.digit5Key.wasPressedThisFrame) ChangeSteepness(-steepnessStep);
            if (keys.digit6Key.wasPressedThisFrame) ChangeSteepness(+steepnessStep);

            if (keys.fKey.wasPressedThisFrame) SetView((view + 1) % ViewNames.Length);
            if (keys.gKey.wasPressedThisFrame) SetDebugVisible(!debugVisible);
            if (keys.hKey.wasPressedThisFrame) showOverlay = !showOverlay;

            // Exponential smoothing of the frame time, so the fps number doesn't flicker.
            smoothedFrameTime = Mathf.Lerp(smoothedFrameTime <= 0f ? Time.unscaledDeltaTime : smoothedFrameTime,
                                           Time.unscaledDeltaTime, 0.05f);
        }

        void OnDisable() => SetView(0); // leave the water shaded

        // ---------------------------------------------------------------
        // Actions
        // ---------------------------------------------------------------

        void ChangeTimeScale(float delta)
        {
            if (simulation == null) return;
            simulation.timeScale = Mathf.Clamp(Mathf.Round((simulation.timeScale + delta) * 10f) / 10f,
                                               timeScaleRange.x, timeScaleRange.y);
        }

        /// <summary>
        /// Move the break threshold by delta metres (public so a test can call the same code as the keys).
        /// Also restarts the events/min measurement, so the HUD shows the rate for the NEW threshold.
        /// </summary>
        public void ChangeThreshold(float delta)
        {
            WaveBreakDetector d = Detector;
            if (d == null) return;
            float h = Mathf.Round((d.threshold + delta) * 10f) / 10f;   // avoid 2.9000001
            d.threshold = Mathf.Max(minThreshold, h);
            ResetRate();
        }

        /// <summary>
        /// Multiply the foam lifetime tau by 'factor' (public so a test can call the same code as the keys).
        /// Takes effect from the next step: foam already on the water simply fades at the new rate.
        /// </summary>
        public void ChangeFoamLifetime(float factor)
        {
            FoamSystem f = Foam;
            if (f == null) return;
            float tau = Mathf.Round(f.decayTime * factor * 100f) / 100f;   // keep it readable: 3.2, 2.56, ...
            f.decayTime = Mathf.Max(minLifetime, tau);
        }

        void ResetRate()
        {
            WaveBreakDetector d = Detector;
            rateStartTime = simulation != null ? simulation.SimTime : 0f;
            rateStartEvents = d != null ? d.TotalEvents : 0;
        }

        /// <summary>Events per minute since the threshold last changed; -1 while there's too little time to tell.</summary>
        public float EventsPerMinute()
        {
            WaveBreakDetector d = Detector;
            if (d == null || simulation == null) return -1f;
            // The clock restarted (t went back, or the counters were cleared): start measuring again.
            if (simulation.SimTime < rateStartTime || d.TotalEvents < rateStartEvents) ResetRate();
            float span = simulation.SimTime - rateStartTime;
            return span < 5f ? -1f : (d.TotalEvents - rateStartEvents) / span * 60f;
        }

        void ChangeSteepness(float delta)
        {
            if (waves == null) return;
            float s = Mathf.Round((waves.steepnessScale + delta) * 100f) / 100f; // avoid 1.0499999
            waves.steepnessScale = Mathf.Clamp(s, 0f, maxSteepnessScale);
            waves.Rebuild(); // recompute k, A, ... and re-upload to the GPU
        }

        void SetView(int newView)
        {
            view = newView;
            if (water == null) return;
            // A MaterialPropertyBlock overrides the value for THIS renderer only, without
            // editing the Water.mat asset. (Changes to material assets made in Play mode are
            // saved to disk, so the demo would otherwise leave the material in a debug view.)
            block ??= new MaterialPropertyBlock();
            water.GetPropertyBlock(block);
            block.SetFloat(DebugViewId, view);
            water.SetPropertyBlock(block);
        }

        void SetDebugVisible(bool visible)
        {
            debugVisible = visible;
            foreach (GameObject go in debugObjects)
                if (go != null) go.SetActive(visible);
        }

        // ---------------------------------------------------------------
        // Overlay (IMGUI): called by Unity, possibly several times per frame
        // ---------------------------------------------------------------

        void OnGUI()
        {
            if (!showOverlay) return;

            if (boxStyle == null)
            {
                background = new Texture2D(1, 1);
                background.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.55f));
                background.Apply();
                boxStyle = new GUIStyle(GUI.skin.box) { normal = { background = background } };
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, normal = { textColor = Color.white } };
            }

            string t = simulation != null ? $"{simulation.SimTime:F1} s" : "-";
            string state = simulation != null && simulation.paused ? "<color=#ffcc66>PAUSED</color>" : "running";
            string speed = simulation != null ? $"x{simulation.timeScale:F1}" : "-";
            string sea = waves != null
                ? $"steepness x{waves.steepnessScale:F2}" +
                  (waves.steepnessScale >= maxSteepnessScale - 1e-4f ? " <color=#ffcc66>(max, E1 limit)</color>" : "") +
                  $"   total {waves.IncidentSteepness:F3}" +
                  (waves.ReflectedCount > 0 ? $" ({waves.TotalSteepness:F3} at the wall, {waves.ReflectedCount} reflected)" : "") +
                  $"   max crest {waves.MaxHeight:F2} m"
                : "-";
            float fps = smoothedFrameTime > 0f ? 1f / smoothedFrameTime : 0f;

            WaveBreakDetector d = Detector;
            string breaks = "-";
            if (d != null)
            {
                float rate = EventsPerMinute();
                breaks = $"break threshold <b>{d.threshold:F1} m</b>   events {d.TotalEvents}   " +
                         $"rejected {d.RejectedEvents}   " +
                         (rate < 0f ? "rate …" : $"{rate:F0} /min") +
                         $"   detector {d.LastTickMs:F2} ms";
            }

            FoamSystem f = Foam;
            string foamLine = f != null
                ? $"foam lifetime τ <b>{f.decayTime:F2} s</b>   spreading D {f.diffusion:F2} m²/s   " +
                  $"splats {f.TotalSplats}   passes/frame {f.SubstepsLastStep}"
                : "-";

            string text =
                "<b>Coastal Wave–Rock Interaction</b>\n" +
                $"time {t}   {state}   speed {speed}   {fps:F0} fps\n" +
                $"{sea}\n" +
                $"{breaks}\n" +
                $"{foamLine}\n" +
                $"view: {ViewNames[view]}   debug objects: {(debugVisible ? "on" : "off")}\n\n" +
                "<b>Camera</b>  right-drag orbit · scroll zoom · WASD / QE / middle-drag pan · Shift fast\n" +
                "<b>Keys</b>  P pause · Space restart · 1/2 threshold · 3/4 foam τ · 5/6 steepness · 7/8 speed · F view · G debug · H hide";

            var content = new GUIContent(text);
            Vector2 size = labelStyle.CalcSize(content);
            GUI.Box(new Rect(10, 10, size.x + 20, size.y + 16), GUIContent.none, boxStyle);
            GUI.Label(new Rect(20, 18, size.x, size.y), content, labelStyle);
        }

        void OnDestroy()
        {
            if (background != null) Destroy(background);
        }
    }
}
