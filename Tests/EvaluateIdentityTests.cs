using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Tests the claim in TECHNICAL.md §4.6 that evaluating the PCHIP curve at its own
    /// newest knot returns the incoming rotation unchanged — the argument used to say
    /// forced snaps are "just resample the raw pose".
    ///
    /// PCHIP does interpolate its knots in exact arithmetic. Two things sit between that
    /// and bitwise identity in the shipped code:
    ///   - MonotoneCubicSampler.Evaluate returns <c>.normalized</c>, a float divide;
    ///   - RebuildIfDirty runs QuaternionSignNorm.Normalise before fitting, which may
    ///     negate the newest sample. q and -q are the same rotation but not the same bits.
    /// </summary>
    public class EvaluateIdentityTests
    {
        private const int BufferSize = 30;
        private const int Samples = 24;
        private const float Dt = 1f / 60f;

        [Test]
        public void EvaluateAtNewestKnot_HemisphereConsistentInput()
        {
            var sampler = new MonotoneCubicSampler(BufferSize);
            Quaternion newest = Quaternion.identity;
            float tEnd = 0f;

            for (int i = 0; i < Samples; i++)
            {
                tEnd = i * Dt;
                newest = SyntheticMotion.Swing(tEnd);
                sampler.Add(tEnd, newest);
            }

            Quaternion evaluated = sampler.Evaluate(tEnd);

            bool bitwise = SyntheticMotion.BitwiseEquals(evaluated, newest);
            float degrees = Quaternion.Angle(evaluated, newest);

            Debug.Log($"[consistent] bitwise={bitwise} angle={degrees:E6} deg\n" +
                      $"  raw       = ({newest.x:R}, {newest.y:R}, {newest.z:R}, {newest.w:R})\n" +
                      $"  evaluated = ({evaluated.x:R}, {evaluated.y:R}, {evaluated.z:R}, {evaluated.w:R})");

            Assert.Less(degrees, 1e-3f,
                "Evaluate at the newest knot should reproduce the newest sample's rotation");
        }

        [Test]
        public void EvaluateAtNewestKnot_HemisphereInconsistentInput()
        {
            var sampler = new MonotoneCubicSampler(BufferSize);
            Quaternion newest = Quaternion.identity;
            float tEnd = 0f;

            for (int i = 0; i < Samples; i++)
            {
                tEnd = i * Dt;
                newest = SyntheticMotion.HemisphereInconsistentSwing(tEnd, i);
                sampler.Add(tEnd, newest);
            }

            Quaternion evaluated = sampler.Evaluate(tEnd);

            bool bitwise = SyntheticMotion.BitwiseEquals(evaluated, newest);
            bool negated = SyntheticMotion.BitwiseEquals(
                evaluated, new Quaternion(-newest.x, -newest.y, -newest.z, -newest.w));
            float degrees = Quaternion.Angle(evaluated, newest);

            Debug.Log($"[inconsistent] bitwise={bitwise} negated={negated} angle={degrees:E6} deg\n" +
                      $"  raw       = ({newest.x:R}, {newest.y:R}, {newest.z:R}, {newest.w:R})\n" +
                      $"  evaluated = ({evaluated.x:R}, {evaluated.y:R}, {evaluated.z:R}, {evaluated.w:R})");

            // Quaternion.Angle treats q and -q as identical, so this holds either way.
            // The interesting result is whether `bitwise` above is true or false.
            Assert.Less(degrees, 1e-3f,
                "hemisphere normalisation must not change the rotation, only possibly its sign");
        }

        /// <summary>
        /// Sweeps the whole stream and reports how often the two differ, so the answer is
        /// a rate rather than a single sample. This is the number §4.6 needs in order to
        /// scope its bit-identity language correctly.
        /// </summary>
        [Test]
        public void ReportEvaluateVsRawDivergenceRate()
        {
            foreach (bool consistent in new[] { true, false })
            {
                var sampler = new MonotoneCubicSampler(BufferSize);
                int checkedTicks = 0, bitwiseEqual = 0, exactlyNegated = 0;
                float maxDegrees = 0f;

                for (int i = 0; i < 600; i++)
                {
                    float t = i * Dt;
                    Quaternion raw = consistent
                        ? SyntheticMotion.Swing(t)
                        : SyntheticMotion.HemisphereInconsistentSwing(t, i);
                    sampler.Add(t, raw);

                    if (!sampler.Ready) continue;

                    Quaternion e = sampler.Evaluate(t);
                    checkedTicks++;
                    if (SyntheticMotion.BitwiseEquals(e, raw)) bitwiseEqual++;
                    if (SyntheticMotion.BitwiseEquals(e, new Quaternion(-raw.x, -raw.y, -raw.z, -raw.w)))
                        exactlyNegated++;
                    maxDegrees = Mathf.Max(maxDegrees, Quaternion.Angle(e, raw));
                }

                Debug.Log($"[{(consistent ? "consistent" : "inconsistent")}] " +
                          $"ticks={checkedTicks} bitwiseEqual={bitwiseEqual} " +
                          $"exactlyNegated={exactlyNegated} maxAngle={maxDegrees:E6} deg");
            }
        }
    }
}
