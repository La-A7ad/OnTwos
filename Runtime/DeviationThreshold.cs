using System;
using System.Collections.Generic;

namespace OnTwos.Runtime.Math
{
    /// <summary>
    /// Deviation-threshold walker — the "stepping" mechanic itself.
    ///
    ///     p_held = sample(tStart)
    ///     emit tStart
    ///     for t in (tStart, tEnd] stepping by dt:
    ///         if |sample(t) - p_held| > τ:
    ///             emit t
    ///             p_held = sample(t)
    ///     emit tEnd if not already emitted
    ///
    /// Same algorithm in both phases. Live animation feeds it a PCHIP fitted over a
    /// rolling window; ragdoll bake feeds it a PCHIP fitted over a recorded
    /// trajectory.
    ///
    /// Operates per scalar channel. For Vector3 / Quaternion bones, run one walker
    /// per component independently — Unity's AnimationClip stores per-channel
    /// curves, so per-channel hold schedules map cleanly to it.
    /// </summary>
    public static class DeviationThreshold
    {
        /// <summary>
        /// Walk a scalar curve and emit timestamps where it has drifted by more
        /// than <paramref name="tau"/> from the last emitted value.
        /// </summary>
        /// <param name="sample">Curve evaluator — typically Pchip.Evaluate.</param>
        /// <param name="tStart">Walk start time (inclusive — always emitted).</param>
        /// <param name="tEnd">Walk end time (inclusive — always emitted).</param>
        /// <param name="dt">Inspection granularity. Smaller = more candidate frames examined.
        /// Typical: 1/60s for 60Hz animation. Does not need to match the original sample rate.</param>
        /// <param name="tau">Crunchiness threshold. Larger = chunkier stepping, fewer holds.</param>
        /// <returns>Ordered hold timestamps. Always contains at least <paramref name="tStart"/> and <paramref name="tEnd"/>.</returns>
        public static List<float> Walk(Func<float, float> sample, float tStart, float tEnd, float dt, float tau)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt), "dt must be positive");
            if (tau <= 0f) throw new ArgumentOutOfRangeException(nameof(tau), "tau must be positive");
            if (tEnd <= tStart) throw new ArgumentException("tEnd must be > tStart");

            var holds = new List<float>(64) { tStart };
            float pHeld = sample(tStart);

            for (float t = tStart + dt; t < tEnd; t += dt)
            {
                float v = sample(t);
                if (System.Math.Abs(v - pHeld) > tau)
                {
                    holds.Add(t);
                    pHeld = v;
                }
            }

            // Always emit the endpoint — final pose must match the source clip's end.
            if (holds[holds.Count - 1] < tEnd) holds.Add(tEnd);

            return holds;
        }
    }
}
