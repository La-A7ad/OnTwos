using System;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Deterministic rotation streams for driving <c>HoldFrameScheduler</c> without a
    /// scene, an Animator or Play mode. The scheduler is a plain C# class, so every
    /// test in this assembly runs in EditMode against synthetic input.
    ///
    /// Quaternions are built from half-angle maths directly rather than via
    /// <c>Quaternion.Euler</c>/<c>AngleAxis</c>, because those canonicalise their output
    /// and would silently hide the hemisphere-consistency cases these tests exist to
    /// exercise.
    /// </summary>
    public static class SyntheticMotion
    {
        /// <summary>
        /// A swing that reverses direction several times over the sampled interval, so
        /// <c>ExtremaDetector</c> has genuine derivative zero-crossings to find. Amplitude
        /// and rate are chosen to cross a 5-15 degree tau many times per second.
        /// </summary>
        public static Quaternion Swing(float t)
        {
            // Two incommensurable frequencies so the extrema do not land on a
            // convenient grid and cannot accidentally coincide with the cadence.
            float degrees = 40f * Mathf.Sin(t * 3.1f) + 12f * Mathf.Sin(t * 7.9f);
            return AboutAxis(new Vector3(0f, 0f, 1f), degrees);
        }

        /// <summary>Slower motion, for exercising the max-hold (forced snap) path.</summary>
        public static Quaternion SlowDrift(float t)
        {
            float degrees = 6f * Mathf.Sin(t * 0.4f);
            return AboutAxis(new Vector3(0f, 1f, 0f), degrees);
        }

        /// <summary>
        /// A stream whose samples are NOT hemisphere-consistent: every third sample is
        /// negated. q and -q are the same rotation, so this is physically identical to
        /// <see cref="Swing"/>, but it forces <c>QuaternionSignNorm.Normalise</c> to do
        /// real work — including, some ticks, negating the newest sample.
        /// </summary>
        public static Quaternion HemisphereInconsistentSwing(float t, int sampleIndex)
        {
            Quaternion q = Swing(t);
            return (sampleIndex % 3 == 0)
                ? new Quaternion(-q.x, -q.y, -q.z, -q.w)
                : q;
        }

        /// <summary>
        /// Build a unit quaternion about <paramref name="axis"/> by
        /// <paramref name="degrees"/>, with no canonicalisation of sign.
        /// </summary>
        public static Quaternion AboutAxis(Vector3 axis, float degrees)
        {
            Vector3 n = axis.normalized;
            float half = degrees * 0.5f * Mathf.Deg2Rad;
            float s = Mathf.Sin(half);
            return new Quaternion(n.x * s, n.y * s, n.z * s, Mathf.Cos(half));
        }

        /// <summary>Exact bitwise equality on all four components, sign included.</summary>
        public static bool BitwiseEquals(Quaternion a, Quaternion b) =>
            BitsOf(a.x) == BitsOf(b.x) &&
            BitsOf(a.y) == BitsOf(b.y) &&
            BitsOf(a.z) == BitsOf(b.z) &&
            BitsOf(a.w) == BitsOf(b.w);

        private static int BitsOf(float f) => BitConverter.SingleToInt32Bits(f);

        /// <summary>
        /// True when the two quaternions express the same rotation, tolerating the
        /// q/-q double cover. Used where semantic equality is the correct assertion
        /// and bitwise equality is not.
        /// </summary>
        public static bool SameRotation(Quaternion a, Quaternion b, float degreesTolerance = 1e-3f) =>
            Quaternion.Angle(a, b) <= degreesTolerance;
    }
}
