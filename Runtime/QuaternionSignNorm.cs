using System;
using UnityEngine;

namespace OnTwos.Runtime.Math
{
    /// <summary>
    /// Quaternion hemisphere normalisation.
    ///
    /// A quaternion q and its negation -q encode the same rotation, but they sit on
    /// opposite hemispheres of the 4-sphere. Per-component interpolation between
    /// hemisphere-inconsistent samples takes the long path (≈ 360° detour), which
    /// PCHIP will faithfully reproduce — producing a visible spin spike in the output.
    ///
    /// Fix: walk the sequence and negate any quaternion whose dot product with its
    /// predecessor is negative. After this, consecutive samples lie on the same
    /// hemisphere and per-component PCHIP fitting is well-behaved.
    ///
    /// After fitting and evaluating, the caller MUST re-normalise the output:
    ///     var q = new Quaternion(px.Evaluate(t), py.Evaluate(t),
    ///                            pz.Evaluate(t), pw.Evaluate(t)).normalized;
    /// Per-component interpolation doesn't preserve unit length.
    /// </summary>
    public static class QuaternionSignNorm
    {
        /// <summary>
        /// Normalise hemisphere consistency in place. The first quaternion is fixed as
        /// the canonical reference; each subsequent one is negated if it would
        /// otherwise take the long path from its predecessor.
        /// </summary>
        public static void Normalise(Quaternion[] q)
        {
            if (q == null) throw new ArgumentNullException(nameof(q));
            Normalise(q, q.Length);
        }

        /// <summary>
        /// Normalise the first <paramref name="count"/> entries only. Lets callers keep
        /// one oversized scratch buffer rather than allocating an exactly-sized array
        /// each time, which matters because this runs per bone per frame.
        /// </summary>
        public static void Normalise(Quaternion[] q, int count)
        {
            if (q == null) throw new ArgumentNullException(nameof(q));
            if (count > q.Length) throw new ArgumentOutOfRangeException(nameof(count));

            for (int i = 1; i < count; i++)
            {
                if (Quaternion.Dot(q[i - 1], q[i]) < 0f)
                    q[i] = new Quaternion(-q[i].x, -q[i].y, -q[i].z, -q[i].w);
            }
        }

        /// <summary>
        /// Non-mutating variant — returns a new array, leaves the input untouched.
        /// Useful in offline processing where you may want both the raw and the
        /// normalised sequence side by side for debugging.
        /// </summary>
        public static Quaternion[] Normalised(Quaternion[] q)
        {
            if (q == null) throw new ArgumentNullException(nameof(q));
            var output = (Quaternion[])q.Clone();
            Normalise(output);
            return output;
        }
    }
}
