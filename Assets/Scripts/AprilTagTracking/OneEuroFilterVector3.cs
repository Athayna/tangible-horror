using UnityEngine;

namespace TangibleHorror.AprilTagTracking
{
    /// <summary>
    /// One Euro filter (Casiez et al.) for a Vector3. Removes marker jitter while staying
    /// responsive on fast motion: the cutoff frequency rises with the measured speed.
    /// Also exposes the smoothed velocity, which is used for latency compensation.
    /// </summary>
    public class OneEuroFilterVector3
    {
        private const float DerivativeCutoff = 1f;

        private Vector3 _smoothed;
        private Vector3 _smoothedDerivative;
        private Vector3 _previousRaw;
        private bool _initialized;

        /// <summary>Smoothed velocity in meters per second.</summary>
        public Vector3 Velocity => _smoothedDerivative;

        /// <summary>Current filter output.</summary>
        public Vector3 Value => _smoothed;

        public void Reset(Vector3 value)
        {
            _smoothed = value;
            _previousRaw = value;
            _smoothedDerivative = Vector3.zero;
            _initialized = true;
        }

        /// <param name="minCutoff">Cutoff at standstill in Hz. Lower = smoother but laggier.</param>
        /// <param name="beta">How much the cutoff opens up with speed. Higher = less lag, more jitter.</param>
        public Vector3 Filter(Vector3 raw, float deltaTime, float minCutoff, float beta)
        {
            if (deltaTime <= 0f)
            {
                deltaTime = 1f / 60f;
            }

            if (!_initialized)
            {
                Reset(raw);
                return _smoothed;
            }

            Vector3 derivative = (raw - _previousRaw) / deltaTime;
            _previousRaw = raw;
            _smoothedDerivative = Vector3.Lerp(_smoothedDerivative, derivative, Alpha(DerivativeCutoff, deltaTime));

            float cutoff = minCutoff + beta * _smoothedDerivative.magnitude;
            _smoothed = Vector3.Lerp(_smoothed, raw, Alpha(cutoff, deltaTime));
            return _smoothed;
        }

        private static float Alpha(float cutoff, float deltaTime)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 0.0001f));
            return 1f / (1f + tau / deltaTime);
        }
    }
}
