using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// The master clock of the scene.
    ///
    /// WHY: the ocean is evaluated twice, on the CPU (C#, to detect wave breaks)
    /// and on the GPU (the water shader, to draw it). Both must use exactly the
    /// same time value, or the waves we detect and the waves we see drift apart.
    ///
    /// WHAT: this component owns the simulation time, advances it once per frame,
    /// publishes it to every shader as the global property "_SimTime", and (in
    /// later steps) updates the other systems in a fixed order:
    /// waves -> wave-break detector -> foam.
    ///
    /// HOW: Update() measures the frame time, clamps it, scales it, and calls
    /// Step(dt). Step() is public so an offline tool can also drive the clock
    /// with a perfectly fixed dt (for smooth video capture).
    /// </summary>
    [DisallowMultipleComponent] // one clock per scene, never two
    public class CoastalSimulation : MonoBehaviour
    {
        // ---------------------------------------------------------------
        // Inspector settings
        // ---------------------------------------------------------------

        [Header("Systems driven by the clock")]
        [Tooltip("The ocean. Receives the simulation time every step.")]
        public WaveField waves;
        [Tooltip("Watches the water at the cliff. Ticked every step, after the waves.")]
        public WaveBreakDetector detector;
        [Tooltip("The foam map. Ticked every step, after the detector (so this step's breaks can add foam).")]
        public FoamSystem foam;
        [Tooltip("White water running up the cliff face (S4.4). Ticked after the detector.")]
        public RunupSystem runup;

        [Header("Time control")]
        [Tooltip("Freeze the simulation. Waves and foam stop exactly where they are.")]
        public bool paused;

        [Tooltip("Simulation speed. 1 = real time, 0.25 = slow motion, 2 = double speed.")]
        [Range(0f, 3f)] public float timeScale = 1f;

        [Tooltip("Largest time step allowed per frame (seconds). Stops the simulation " +
                 "from jumping forward after a hitch, e.g. while the Editor compiles.")]
        [Range(0.01f, 0.25f)] public float maxStep = 0.1f;

        [Header("Read-only (for debugging)")]
        [Tooltip("Current simulation time in seconds. Written by the clock every frame; " +
                 "editing it here has no lasting effect.")]
        [SerializeField] float simTimeDisplay;

        [Header("Debug")]
        [Tooltip("Write clock events (start, pause, resume, restart) and a periodic " +
                 "status line to the Console.")]
        public bool debugLogs = true;

        [Tooltip("Seconds of simulation time between status lines (0 = no status lines).")]
        [Min(0f)] public float statusInterval = 5f;

        // ---------------------------------------------------------------
        // Public state
        // ---------------------------------------------------------------

        /// <summary>Simulation time in seconds since the last restart.</summary>
        public float SimTime { get; private set; }

        /// <summary>Number of steps taken since the last restart.</summary>
        public int StepCount { get; private set; }

        // Shader.PropertyToID turns the property name into an integer once,
        // which is faster than passing the string "_SimTime" every frame.
        static readonly int SimTimeId = Shader.PropertyToID("_SimTime");

        // Debug bookkeeping: what "paused" was last frame (to detect a change),
        // and when the next status line is due.
        bool wasPaused;
        float nextStatusTime;

        // All our Console messages start with this tag, so they're easy to filter:
        // type "[Clock]" into the Console's search box.
        const string Tag = "[Clock] ";

        // ---------------------------------------------------------------
        // Unity callbacks
        // ---------------------------------------------------------------

        // Called once when the scene starts playing.
        void Start()
        {
            Restart();
            if (debugLogs)
                Debug.Log(Tag + $"Started. timeScale = {timeScale}, maxStep = {maxStep} s", this);
        }

        // Called once per rendered frame.
        void Update()
        {
            // Log when the user ticks or unticks "paused" in the Inspector.
            if (debugLogs && paused != wasPaused)
                Debug.Log(Tag + (paused ? "Paused" : "Resumed") + $" at t = {SimTime:F2} s", this);
            wasPaused = paused;

            if (paused) return;

            // Time.deltaTime = seconds since the previous frame (~0.016 at 60 fps).
            // Clamp it so one slow frame cannot cause a huge jump, then apply the
            // user's speed setting.
            float dt = Mathf.Min(Time.deltaTime, maxStep) * timeScale;
            Step(dt);
        }

        // ---------------------------------------------------------------
        // Clock API
        // ---------------------------------------------------------------

        /// <summary>
        /// Advance the whole simulation by dt seconds. Everything that depends on
        /// time is updated from here, always in the same order.
        /// </summary>
        public void Step(float dt)
        {
            SimTime += dt;
            StepCount++;
            simTimeDisplay = SimTime;

            // 1. Publish the time to the GPU, so every shader sees the same t.
            Shader.SetGlobalFloat(SimTimeId, SimTime);

            // Debug: a status line every few seconds of simulation time.
            // (Logging every frame would flood the Console with ~60+ lines per second.)
            if (debugLogs && statusInterval > 0f && SimTime >= nextStatusTime)
            {
                Debug.Log(Tag + $"t = {SimTime:F2} s, steps = {StepCount}, " +
                          $"avg step = {SimTime / Mathf.Max(1, StepCount) * 1000f:F2} ms, " +
                          $"GPU _SimTime = {Shader.GetGlobalFloat(SimTimeId):F2}", this);
                nextStatusTime += statusInterval;
            }

            // 2. Waves: every CPU wave query this frame uses the same t as the GPU.
            if (waves != null) waves.SetTime(SimTime);
            // 3. Wave-break check: probes read the water height at the cliff (same t).
            if (detector != null) detector.Tick(SimTime);
            // 4. Foam: old foam fades by exp(-dt/tau), new splats are added (GPU).
            if (foam != null) foam.Tick(dt);
            // 5. Run-up: jets launched by this step's breaks climb the cliff face (GPU map).
            if (runup != null) runup.Tick(dt);
        }

        /// <summary>Reset the clock to t = 0. Right-click the component header and pick "Restart".</summary>
        [ContextMenu("Restart")]
        public void Restart()
        {
            SimTime = 0f;
            StepCount = 0;
            simTimeDisplay = 0f;
            nextStatusTime = statusInterval;
            Shader.SetGlobalFloat(SimTimeId, 0f);
            if (waves != null) waves.SetTime(0f);
            if (detector != null) detector.ResetDetection();   // forget probe states and event counts
            if (foam != null) foam.Clear();                    // start with clean water
            if (runup != null) runup.Clear();                  // ... and a dry cliff
            if (debugLogs) Debug.Log(Tag + "Restarted: t = 0", this);
        }
    }
}
