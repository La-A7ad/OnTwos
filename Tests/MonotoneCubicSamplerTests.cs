using System;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// The sampler is the only stateful thing under the scheduler, and its failure modes
    /// are all silent: a ring buffer that unrolls in the wrong order, a LUT read before
    /// it is valid, a stale fit surviving a Clear. None of those throw — they produce
    /// plausible-looking wrong poses, which is why they need pinning here rather than
    /// being caught downstream.
    /// </summary>
    public class MonotoneCubicSamplerTests
    {
        private static Quaternion Rot(float deg) =>
            SyntheticMotion.AboutAxis(Vector3.forward, deg);

        private static MonotoneCubicSampler Filled(int count, int capacity = 30, float dt = 0.016f)
        {
            var s = new MonotoneCubicSampler(capacity);
            for (int i = 0; i < count; i++) s.Add(i * dt, Rot(20f * Mathf.Sin(i * 0.3f)));
            return s;
        }

        [Test]
        public void Constructor_CapacityBelowMinSamples_Throws()
        {
            Assert.Throws<ArgumentException>(() => new MonotoneCubicSampler(3),
                "a buffer that can never become Ready must be rejected at construction");
        }

        [Test]
        public void Ready_IsFalseUntilFourSamples()
        {
            var s = new MonotoneCubicSampler(30);
            for (int i = 0; i < 3; i++)
            {
                s.Add(i * 0.016f, Rot(i));
                Assert.IsFalse(s.Ready, $"Ready must stay false at Count={s.Count}");
            }
            s.Add(3 * 0.016f, Rot(3));
            Assert.IsTrue(s.Ready);
        }

        [Test]
        public void Count_SaturatesAtCapacity()
        {
            var s = Filled(100, capacity: 12);
            Assert.AreEqual(12, s.Count);
        }

        [Test]
        public void OldestTime_TracksTheRingBufferAfterWraparound()
        {
            const int cap = 8;
            const float dt = 0.1f;
            var s = new MonotoneCubicSampler(cap);

            for (int i = 0; i < 20; i++)
            {
                s.Add(i * dt, Rot(i));
                int oldestIndex = Mathf.Max(0, i - cap + 1);
                Assert.AreEqual(oldestIndex * dt, s.OldestTime, 1e-5f,
                    $"after {i + 1} adds the oldest retained sample is index {oldestIndex}");
            }
        }

        [Test]
        public void Evaluate_BeforeReady_ReturnsTheNewestRawSample()
        {
            var s = new MonotoneCubicSampler(30);
            s.Add(0f, Rot(0f));
            Quaternion newest = Rot(31f);
            s.Add(0.016f, newest);

            Assert.IsTrue(SyntheticMotion.BitwiseEquals(newest, s.Evaluate(999f)),
                "below MinSamples the sampler must hand back the raw sample untouched");
        }

        [Test]
        public void Evaluate_EmptyBuffer_ReturnsIdentity()
        {
            var s = new MonotoneCubicSampler(30);
            Assert.IsTrue(SyntheticMotion.BitwiseEquals(Quaternion.identity, s.Evaluate(0f)));
        }

        [Test]
        public void Evaluate_AtAKnot_MatchesTheInputAsARotation()
        {
            // Not bitwise: per-component fitting plus renormalisation, and sign
            // normalisation may have negated the sample. Equality as a ROTATION is the
            // correct assertion and the one the scheduler depends on.
            var s = new MonotoneCubicSampler(30);
            for (int i = 0; i < 10; i++) s.Add(i * 0.016f, Rot(15f * Mathf.Sin(i * 0.4f)));

            float tKnot = 6 * 0.016f;
            Quaternion expected = Rot(15f * Mathf.Sin(6 * 0.4f));
            Assert.LessOrEqual(Quaternion.Angle(expected, s.Evaluate(tKnot)), 1e-2f);
        }

        [Test]
        public void Evaluate_ReturnsUnitQuaternions()
        {
            var s = Filled(20);
            for (float t = 0f; t < 0.3f; t += 0.007f)
            {
                Quaternion q = s.Evaluate(t);
                float len = new Vector4(q.x, q.y, q.z, q.w).magnitude;
                Assert.AreEqual(1f, len, 1e-4f,
                    "component-wise interpolation does not preserve unit length; " +
                    "Evaluate must renormalise");
            }
        }

        [Test]
        public void DuplicateTimestamps_AreDroppedRatherThanFitted()
        {
            // A paused game (timeScale 0) feeds LateUpdate the same Time.time every
            // frame. The dedupe is what stops Pchip.Fit throwing from inside the
            // per-frame loop.
            var s = new MonotoneCubicSampler(30);
            for (int i = 0; i < 12; i++) s.Add(1.25f, Rot(i));

            Assert.DoesNotThrow(() => s.Evaluate(1.25f));
            Assert.DoesNotThrow(() => s.Derivative(1.25f));
            Assert.AreEqual(0, s.ArcLengthCandidates(1.0f, 1.5f, 2, new float[4]),
                "with fewer than two distinct times there is no valid LUT to read");
        }

        [Test]
        public void NonIncreasingTimestamps_AreDropped()
        {
            var s = new MonotoneCubicSampler(30);
            s.Add(0.0f, Rot(0));
            s.Add(0.1f, Rot(10));
            s.Add(0.05f, Rot(20));  // out of order — must not corrupt the fit
            s.Add(0.2f, Rot(30));
            s.Add(0.3f, Rot(40));

            Assert.DoesNotThrow(() => s.Evaluate(0.25f));
        }

        [Test]
        public void Clear_InvalidatesTheFitAndTheLut()
        {
            var s = Filled(20);
            Assert.Greater(s.ArcLengthCandidates(0.05f, 0.25f, 2, new float[4]), 0);

            s.Clear();

            Assert.AreEqual(0, s.Count);
            Assert.IsFalse(s.Ready);
            Assert.AreEqual(0, s.ArcLengthCandidates(0.05f, 0.25f, 2, new float[4]),
                "a cleared sampler must not serve candidates from the pre-Clear LUT");
            Assert.IsTrue(SyntheticMotion.BitwiseEquals(Quaternion.identity, s.Evaluate(0.1f)));
        }

        [Test]
        public void ArcLengthCandidates_BeforeAnyFit_ReturnsZeroAndWritesNothing()
        {
            var s = new MonotoneCubicSampler(30);
            var dest = new float[4];
            for (int i = 0; i < dest.Length; i++) dest[i] = -1f;

            Assert.AreEqual(0, s.ArcLengthCandidates(0f, 1f, 2, dest));
            Assert.AreEqual(-1f, dest[0], "dest must be left untouched when the LUT is invalid");
        }

        [Test]
        public void ArcLengthCandidates_AreStrictlyInteriorToTheSegment()
        {
            // This is load-bearing: because placement never reaches a or b, the
            // scheduler has to add segment boundaries as candidates separately. If this
            // ever stopped being true, that compensation would double up on the ends.
            var s = Filled(25);
            var dest = new float[4];
            const float a = 0.08f, b = 0.30f;

            int n = s.ArcLengthCandidates(a, b, 3, dest);
            Assert.AreEqual(3, n);
            for (int i = 0; i < n; i++)
            {
                Assert.Greater(dest[i], a, $"candidate {i} reached the lower boundary");
                Assert.Less(dest[i], b, $"candidate {i} reached the upper boundary");
            }
        }

        [Test]
        public void ArcLengthCandidates_AreOrderedAndDistinct()
        {
            var s = Filled(25);
            var dest = new float[4];
            int n = s.ArcLengthCandidates(0.08f, 0.30f, 4, dest);

            for (int i = 1; i < n; i++)
                Assert.Greater(dest[i], dest[i - 1],
                    "equal-angle placement must produce increasing times");
        }

        [Test]
        public void ArcLengthCandidates_OnAFlatSegment_FallsBackToEqualTime()
        {
            // A stationary bone carries no arc length, so the table has no information
            // to invert. Equal-time spacing is the documented fallback.
            var s = new MonotoneCubicSampler(30);
            Quaternion still = Rot(11f);
            for (int i = 0; i < 20; i++) s.Add(i * 0.016f, still);

            var dest = new float[4];
            int n = s.ArcLengthCandidates(0.05f, 0.25f, 3, dest);

            Assert.AreEqual(3, n);
            float expectedStep = (0.25f - 0.05f) / 4f;
            for (int i = 0; i < 3; i++)
                Assert.AreEqual(0.05f + expectedStep * (i + 1), dest[i], 1e-4f);
        }

        [Test]
        public void ArcLengthCandidates_DestTooSmall_Throws()
        {
            var s = Filled(20);
            Assert.Throws<ArgumentException>(() => s.ArcLengthCandidates(0f, 0.2f, 4, new float[2]));
        }

        [Test]
        public void ArcLengthCandidates_NullDest_Throws()
        {
            var s = Filled(20);
            Assert.Throws<ArgumentNullException>(() => s.ArcLengthCandidates(0f, 0.2f, 2, null));
        }

        [Test]
        public void ArcLengthCandidates_NonPositiveN_ReturnsZero()
        {
            var s = Filled(20);
            Assert.AreEqual(0, s.ArcLengthCandidates(0f, 0.2f, 0, new float[4]));
            Assert.AreEqual(0, s.ArcLengthCandidates(0f, 0.2f, -3, new float[4]));
        }

        [Test]
        public void ArcLengthCandidates_ReversedInterval_DoesNotThrowOrEmitNaN()
        {
            var s = Filled(20);
            var dest = new float[4];
            Assert.DoesNotThrow(() => s.ArcLengthCandidates(0.25f, 0.05f, 2, dest));
            for (int i = 0; i < 2; i++)
                Assert.IsFalse(float.IsNaN(dest[i]), "a reversed interval must not produce NaN times");
        }

        [Test]
        public void SteadyState_AddAndEvaluate_DoNotAllocate()
        {
            var s = Filled(40);
            var dest = new float[4];          // hoisted: a per-iteration array would be
            s.Evaluate(0.3f);                 // indistinguishable from an internal allocation

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 40; i < 400; i++)
            {
                s.Add(i * 0.016f, Rot(20f * Mathf.Sin(i * 0.3f)));
                s.Evaluate(i * 0.016f);
                s.ArcLengthCandidates((i - 10) * 0.016f, i * 0.016f, 2, dest);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, bytes, $"steady-state sampling allocated {bytes} B");
        }
    }
}
