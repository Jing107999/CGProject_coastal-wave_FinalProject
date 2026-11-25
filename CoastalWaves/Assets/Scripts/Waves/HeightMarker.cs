using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// DEBUG / VERIFICATION TOOL: shows the water height at ONE fixed world point.
    ///
    /// WHY: wave-break detection (S2) asks "how high is the water HERE?" at fixed points
    /// next to the rock. This marker makes that answer visible, and shows how wrong the
    /// naive shortcut would be.
    ///
    /// WHAT: two child spheres pinned to this object's (x, z):
    ///  - "Correct" (white): y = WaveField.HeightAt(x, z, t), i.e. WITH inversion.
    ///    It should bob straight up and down and sit exactly on the rendered water.
    ///  - "Naive" (red, optional): y = Evaluate(x, z, t).y, i.e. pretending the rest
    ///    position is the world position. It floats above or sinks below the water.
    /// A Gizmo line joins them, and the gap is logged every few seconds.
    /// </summary>
    [ExecuteAlways]
    public class HeightMarker : MonoBehaviour
    {
        public WaveField waveField;

        [Tooltip("Sphere placed at the correct (inverted) height.")]
        public Transform correctMarker;

        [Tooltip("Optional sphere placed at the naive height, to show the error.")]
        public Transform naiveMarker;

        [Tooltip("Fixed-point iterations used for the correct marker.")]
        [Range(0, 12)] public int iterations = WaveField.DefaultInversionIterations;

        [Header("Debug")]
        public bool debugLogs = true;
        [Min(0.5f)] public float logInterval = 5f;

        const string Tag = "[Height] ";
        float nextLogTime;
        float maxGap; // largest naive-vs-correct difference seen since the last log

        void LateUpdate()   // after the clock has advanced the time
        {
            if (waveField == null) return;
            float t = waveField.SimTime;
            Vector3 p = transform.position;   // the fixed world point (only x and z matter)

            float correctY = waveField.HeightAt(p.x, p.z, t, iterations);
            float naiveY = waveField.Evaluate(p.x, p.z, t).position.y;

            // Both spheres stay at the same (x, z); only their height changes.
            if (correctMarker != null) correctMarker.position = new Vector3(p.x, correctY, p.z);
            if (naiveMarker != null) naiveMarker.position = new Vector3(p.x, naiveY, p.z);

            maxGap = Mathf.Max(maxGap, Mathf.Abs(naiveY - correctY));
            if (debugLogs && Application.isPlaying && t >= nextLogTime)
            {
                Debug.Log(Tag + $"at ({p.x:F1}, {p.z:F1}): water height = {correctY:F3} m " +
                          $"({iterations} iterations); naive height = {naiveY:F3} m; " +
                          $"largest naive error in the last {logInterval:F0} s = {maxGap * 100f:F1} cm", this);
                maxGap = 0f;
                nextLogTime = t + logInterval;
            }
        }

        void OnDrawGizmos()
        {
            if (correctMarker == null || naiveMarker == null) return;
            Gizmos.color = Color.red;   // the size of the naive error
            Gizmos.DrawLine(correctMarker.position, naiveMarker.position);
        }
    }
}
