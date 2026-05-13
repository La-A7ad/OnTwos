using System;
using System.Collections.Generic;

namespace CrunchyRagdoll.Runtime.Math
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
    /// </summary>
    public static class ExtremaDetector
    {
        // Brent's method convergence tolerance — sub-millisecond precision.
        private const float BrentTol = 1e-4f;
        private const int BrentMaxIter = 50;

        // Minimum segment length — extrema closer than this are discarded
        // as numerical noise (e.g. near-flat segments).
        private const float MinSegment = 0.016f; // ~1 frame at 60Hz

        /// <summary>
        /// Find all extrema of a scalar PCHIP curve's derivative over [tStart, tEnd].
        /// </summary>
        /// <param name="derivative">Derivative function — typically pchip.Derivative.</param>
        /// <param name="tStart">Search start.</param>
        /// <param name="tEnd">Search end.</param>
        /// <param name="dt">Coarse scan step. Smaller catches closer extrema.</param>
        public static List<float> Find(
            Func<float, float> derivative,
            float tStart, float tEnd, float dt = 0.016f)
        {
            if (derivative == null) throw new ArgumentNullException(nameof(derivative));
            if (tEnd <= tStart) return new List<float>();

            List<float> extrema = new List<float>(16);

            float prev = derivative(tStart);
            float tPrev = tStart;

            for (float t = tStart + dt; t <= tEnd + 1e-6f; t += dt)
            {
                t = System.Math.Min(t, tEnd);
                float curr = derivative(t);

                // Sign change detected — bracket contains a zero crossing.
                if (prev * curr < 0f)
                {
                    float root = Brent(derivative, tPrev, t);

                    // Discard if too close to previous extremum (noise filter).
                    if (extrema.Count == 0 ||
                        root - extrema[extrema.Count - 1] >= MinSegment)
                    {
                        extrema.Add(root);
                    }
                }

                prev = curr;
                tPrev = t;
            }

            return extrema;
        }

        /// <summary>
        /// Find extrema across all four quaternion components of a sampler,
        /// then merge and sort into a single timeline.
        /// A bone's motion extremum occurs when ANY component's derivative hits zero.
        /// </summary>
        public static List<float> FindForBone(
            MonotoneCubicSampler sampler,
            float tStart, float tEnd, float dt = 0.016f)
        {
            if (sampler == null) throw new ArgumentNullException(nameof(sampler));

            List<float> all = new List<float>(32);

            // Run detector on each quaternion component independently.
            all.AddRange(Find(t => sampler.Derivative(t).x, tStart, tEnd, dt));
            all.AddRange(Find(t => sampler.Derivative(t).y, tStart, tEnd, dt));
            all.AddRange(Find(t => sampler.Derivative(t).z, tStart, tEnd, dt));
            all.AddRange(Find(t => sampler.Derivative(t).w, tStart, tEnd, dt));

            all.Sort();

            // Merge extrema that are too close together across components.
            List<float> merged = new List<float>(all.Count);
            foreach (float e in all)
            {
                if (merged.Count == 0 ||
                    e - merged[merged.Count - 1] >= MinSegment)
                {
                    merged.Add(e);
                }
            }

            return merged;
        }

        /// <summary>
        /// Brent's method — finds root of f in [a, b] where f(a) and f(b)
        /// have opposite signs. Guaranteed convergence, superlinear near root.
        /// </summary>
        private static float Brent(Func<float, float> f, float a, float b)
        {
            float fa = f(a);
            float fb = f(b);

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

                float fs = f(s);
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
