using System;
using UnityEngine;

namespace Coastal
{
    /// <summary>
    /// The settings of ONE Gerstner wave, as you edit them in the Inspector.
    ///
    /// These are the "human" numbers (degrees, metres). WaveField converts them
    /// into the numbers the formula needs (direction vector, k, A, omega).
    /// </summary>
    [Serializable] // lets Unity show a list of these in the Inspector and save them in the scene
    public struct GerstnerWave
    {
        [Tooltip("Direction the wave travels, in degrees. 0 = towards +X (the rock), " +
                 "90 = towards +Z.")]
        public float directionDeg;

        [Tooltip("Crest-to-crest distance L in metres. Longer waves travel faster.")]
        [Min(0.5f)] public float wavelength;

        [Tooltip("Steepness s = k * A. 0 = flat; close to 1 = pointed crests. " +
                 "The SUM over all waves must stay below 1 or the surface folds.")]
        [Range(0f, 1f)] public float steepness;

        [Tooltip("Phase offset in radians. Shifts the wave so the components " +
                 "don't all peak at the same place at t = 0.")]
        public float phase;

        public GerstnerWave(float directionDeg, float wavelength, float steepness, float phase = 0f)
        {
            this.directionDeg = directionDeg;
            this.wavelength = wavelength;
            this.steepness = steepness;
            this.phase = phase;
        }
    }

    /// <summary>
    /// Everything the wave formula tells us about one water particle at one moment.
    /// </summary>
    public struct WaveSample
    {
        /// <summary>Where the particle is now (world space).</summary>
        public Vector3 position;

        /// <summary>Unit vector pointing straight out of the surface (for lighting).</summary>
        public Vector3 normal;

        /// <summary>How fast and where the water is moving (m/s). Used by wave-break detection.</summary>
        public Vector3 velocity;

        /// <summary>
        /// Horizontal squeeze of the surface: 1 = unchanged, below 1 = compressed
        /// (crest), 0 = about to fold. Used for whitecap foam.
        /// </summary>
        public float jacobian;
    }
}
