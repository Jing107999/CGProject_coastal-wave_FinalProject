using UnityEngine;
using UnityEngine.InputSystem;

namespace Coastal
{
    /// <summary>
    /// A camera that circles a target point (the rock), steered with mouse and keyboard.
    ///
    /// WHY: the demo must be explorable. An orbit camera suits a scene centred on one spot.
    ///
    /// WHAT / HOW:
    ///   rotation = (pitch, yaw)                       right-mouse drag
    ///   distance                                      scroll wheel (multiplies, so zoom feels even)
    ///   target                                        W A S D / Q E / middle-mouse drag (Shift = 3x)
    ///   position = target - rotation * forward * distance
    /// The camera is then lifted, if needed, to stay a margin above the ACTUAL wave
    /// surface at its position (WaveField.HeightAt, see S1.5), so it never dips into a trough.
    /// Movement uses real time (unscaledDeltaTime), so it still works while the
    /// simulation is paused or in slow motion.
    /// </summary>
    [DisallowMultipleComponent]
    public class OrbitCamera : MonoBehaviour
    {
        [Header("Pose (set by the scene builder to match the starting view)")]
        public Vector3 target = new Vector3(0f, 1f, 0f);
        [Tooltip("Rotation around the vertical axis, degrees.")]
        public float yaw = 56.3f;
        [Tooltip("Tilt above the horizon, degrees (positive = looking down).")]
        public float pitch = 8.4f;
        [Min(0.1f)] public float distance = 54.7f;

        [Header("Limits")]
        public Vector2 pitchRange = new Vector2(-5f, 85f);
        public Vector2 distanceRange = new Vector2(4f, 250f);

        [Header("Speeds")]
        [Tooltip("Degrees of rotation per pixel of mouse movement.")]
        public float orbitSpeed = 0.25f;
        [Tooltip("Fraction of the distance zoomed per scroll notch.")]
        [Range(0.01f, 0.5f)] public float zoomStep = 0.1f;
        [Tooltip("Pan speed in metres per second at a distance of 45 m (scales with distance).")]
        public float panSpeed = 20f;

        [Header("Stay above the water")]
        [Tooltip("Optional. If set, the camera stays above the real wave surface at its position.")]
        public WaveField waveField;
        [Tooltip("Minimum clearance above the water surface (metres). Small = low, dramatic shots.")]
        [Min(0f)] public float clearance = 0.6f;
        [Tooltip("Lowest the CAMERA may go: this many metres above the calm sea level. " +
                 "Q (and zoom / orbit) lowers the whole view until the camera reaches this height.")]
        [Min(0f)] public float minCameraHeight = 2f;

        void OnEnable() => Apply();

        // LateUpdate: after the clock and the waves have moved this frame.
        void LateUpdate()
        {
            Mouse mouse = Mouse.current;       // null if no mouse is connected
            Keyboard keys = Keyboard.current;  // null if no keyboard is connected
            float dt = Time.unscaledDeltaTime; // REAL seconds, unaffected by pause / slow motion

            // 1. ORBIT: right mouse button held -> mouse movement turns the camera.
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();          // pixels moved this frame
                yaw += delta.x * orbitSpeed;
                pitch = Mathf.Clamp(pitch - delta.y * orbitSpeed, pitchRange.x, pitchRange.y);
            }

            // 2. ZOOM: each scroll notch changes the distance by a fixed fraction.
            if (mouse != null)
            {
                float notches = mouse.scroll.ReadValue().y / 120f; // one wheel notch reports about 120
                distance = Mathf.Clamp(distance * (1f - notches * zoomStep), distanceRange.x, distanceRange.y);
            }

            // 3. PAN: move the target along the ground (relative to where we look), or up/down.
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 flatForward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, flatForward);

            Vector3 move = Vector3.zero;
            if (keys != null)
            {
                if (keys.wKey.isPressed) move += flatForward;
                if (keys.sKey.isPressed) move -= flatForward;
                if (keys.dKey.isPressed) move += right;
                if (keys.aKey.isPressed) move -= right;
                if (keys.eKey.isPressed) move += Vector3.up;
                if (keys.qKey.isPressed) move -= Vector3.up;
            }
            if (mouse != null && mouse.middleButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                move += (-right * delta.x - flatForward * delta.y) * 0.05f; // drag the scene under the cursor
            }
            bool fast = keys != null && keys.leftShiftKey.isPressed;
            float speed = panSpeed * (fast ? 3f : 1f) * (distance / 45f); // same feel zoomed in or out
            target += move * (speed * dt);

            // S5.9c: limit the CAMERA's height, not the target's. (Before: the target was kept
            // above sea level - 0.5 m. With the target on the sea surface and the camera 55 m up,
            // Q could hardly move anything.) Now Q lowers camera and target together, keeping
            // the same view angle, until the camera is minCameraHeight above the calm sea.
            // The target may then lie below the water; that's fine, it's only the orbit pivot.
            // Because target and camera stop together, holding Q can't sink the target on its
            // own (the old "view tilts into a wave face" problem stays fixed).
            if (waveField != null)
            {
                float cameraY = target.y - (rotation * Vector3.forward).y * distance;   // = Apply()'s height
                float lowest = waveField.seaLevel + minCameraHeight;
                if (cameraY < lowest) target.y += lowest - cameraY;   // lift the whole view back up
            }

            Apply();
        }

        /// <summary>Place the camera from target, yaw, pitch and distance.</summary>
        public void Apply()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 position = target - rotation * Vector3.forward * distance;

            // Keep above the real wave surface at this (x, z), not just above y = 0.
            if (waveField != null)
            {
                float water = waveField.HeightAt(position.x, position.z, waveField.SimTime);
                position.y = Mathf.Max(position.y, water + clearance);
            }

            transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position));
        }

        /// <summary>
        /// Work out yaw, pitch and distance from the camera's current position, so that
        /// switching this component on doesn't make the view jump. Used by the scene builder.
        /// </summary>
        public void MatchCurrentView(Vector3 newTarget)
        {
            target = newTarget;
            Vector3 toTarget = target - transform.position;
            distance = toTarget.magnitude;
            yaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            pitch = Mathf.Asin(-toTarget.y / distance) * Mathf.Rad2Deg;
        }
    }
}
