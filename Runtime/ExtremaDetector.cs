using System;
using System.Collections.Generic;
using UnityEngine;

namespace OnTwos.Runtime.Math
{
    /// <summary>
    /// Detects extrema (derivative zero crossings) in a PCHIP curve.
    ///
    /// Algorithm:
    ///   1. Sample the derivative at regular intervals over [tStart, tEnd].
    ///   2. Detect sign changes — each sign change brackets a zero crossing.
    ///   3. Refine each bracket with Brent's method for precise location.
    ///
    /// Extrema are where bone motion peaks or reverses — mathematically
    /// optimal positions to place hold-frame boundaries.
    ///
    /// All four quaternion components are scanned in a single pass. Scanning them
    /// separately meant calling <see cref="MonotoneCubicSampler.Derivative"/> — which
    /// evaluates all four components regardless — four times per sample point and
    /// discarding three quarters of each result.
    /// </summary>
    public static class ExtremaDetector
    {
        // Brent's method convergence tolerance — sub-millisecond precision.
        private const float BrentTol = 1e-4f;
        private const int BrentMaxIter = 50;

        // Minimum segment length — extrema closer than this are discarded
        // as numerical noise (e.g. near-flat segments).
        private const float MinSegment = 0.016f; // ~1 frame at 60Hz

        // Unsorted roots from all four components, reused across calls. This runs per
        // bone per extrema interval, so a fresh list here was a per-bone allocation.
        // ThreadStatic because the type is a public static utility: the steppers only
        // ever call it from the main thread, but a static mutable buffer that silently
        // corrupts under a worker thread is a bad thing to leave lying around.
        [ThreadStatic] private static List<float> _scratch;

        /// <summary>
        /// Find extrema across all four quaternion components of a sampler, merged and
        /// sorted into a single timeline. A bone's motion extremum occurs when ANY
        /// component's derivative hits zero.
        ///
        /// Results are appended to <paramref name="results"/> after clearing it. The
        /// caller owns the list so repeated calls do not allocate.
        /// </summary>
        public static void FindForBone(
            MonotoneCubicSampler sampler,
            float tStart, float tEnd,
            List<float> results,
            float dt = 0.016f)
        {
            if (sampler == null) throw new ArgumentNullException(nameof(sampler));
            if (results == null) throw new ArgumentNullException(nameof(results));

            results.Clear();
            if (tEnd <= tStart) return;

            var all = _scratch ??= new List<float>(32);
            all.Clear();

            Vector4 prev = sampler.Derivative(tStart);
            float tPrev = tStart;

            for (float t = tStart + dt; t <= tEnd + 1e-6f; t += dt)
            {
                t = System.Math.Min(t, tEnd);
                Vector4 curr = sampler.Derivative(t);

                // A sign change on any component brackets a zero crossing on that
                // component. Refine each one independently — they land at different
                // times and the merge pass below reconciles them.
                for (int c = 0; c < 4; c++)
                {
                    if (prev[c] * curr[c] < 0f)
                        all.Add(Brent(sampler, c, tPrev, t));
                }

                prev = curr;
                tPrev = t;
            }

            if (all.Count == 0) return;

            all.Sort();

            // Merge extrema that land closer together than one frame — across
            // components as well as within one, since two components crossing zero a
            // fraction of a millisecond apart describe the same turning point.
            for (int i = 0; i < all.Count; i++)
            {
                float e = all[i];
                if (results.Count == 0 || e - results[results.Count - 1] >= MinSegment)
                    results.Add(e);
            }
        }

        /// <summary>
        /// One component of the sampler's derivative. Kept as an explicit index rather
        /// than a <see cref="Func{T, TResult}"/> so the scan and Brent's refinement
        /// allocate no closures.
        /// </summary>
        private static float Deriv(MonotoneCubicSampler sampler, int component, float t)
            => sampler.Derivative(t)[component];

        /// <summary>
        /// Brent's method — finds the root of one derivative component in [a, b], where
        /// the component has opposite signs at the two ends. Guaranteed convergence,
        /// superlinear near the root.
        /// </summary>
        private static float Brent(MonotoneCubicSampler sampler, int component, float a, float b)
        {
            float fa = Deriv(sampler, component, a);
            float fb = Deriv(sampler, component, b);

            if (fa * fb > 0f)
                return (a + b) * 0.5f; // Shouldn't happen — return midpoint as fallback.

            if (System.Math.Abs(fa) < System.Math.Abs(fb))
            {
                Swap(ref a, ref b);
                Swap(ref fa, ref fb);
            }

            float c = a, fc = fa;
            bool mflag = true;
            float d = 0f;

            for (int i = 0; i < BrentMaxIter; i++)
            {
                if (System.Math.Abs(b - a) < BrentTol) break;

                float s;
                if (fa != fc && fb != fc)
                {
                    // Inverse quadratic interpolation.
                    s = (a * fb * fc / ((fa - fb) * (fa - fc)))
                      + (b * fa * fc / ((fb - fa) * (fb - fc)))
                      + (c * fa * fb / ((fc - fa) * (fc - fb)));
                }
                else
                {
                    // Secant method.
                    s = b - (fb * (b - a) / (fb - fa));
                }

                float cond1 = ((3f * a) + b) / 4f;
                bool bad = s < System.Math.Min(cond1, b) ||
                               s > System.Math.Max(cond1, b)
                           || (mflag && System.Math.Abs(s - b) >= System.Math.Abs(b - c) / 2f)
                           || (!mflag && System.Math.Abs(s - b) >= System.Math.Abs(c - d) / 2f)
                           || (mflag && System.Math.Abs(b - c) < BrentTol)
                           || (!mflag && System.Math.Abs(c - d) < BrentTol);

                if (bad)
                {
                    s = (a + b) / 2f; // Bisection fallback.
                    mflag = true;
                }
                else
                {
                    mflag = false;
                }

                float fs = Deriv(sampler, component, s);
                d = c; c = b; fc = fb;

                if (fa * fs < 0f) { b = s; fb = fs; }
                else { a = s; fa = fs; }

                if (System.Math.Abs(fa) < System.Math.Abs(fb))
                {
                    Swap(ref a, ref b);
                    Swap(ref fa, ref fb);
                }
            }

            return b;
        }

        private static void Swap(ref float a, ref float b)
        {
            (b, a) = (a, b);
        }
    }
}
