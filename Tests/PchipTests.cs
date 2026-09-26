using System;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Fritsch-Carlson is a published algorithm, so these do not re-derive it. They pin
    /// the three properties the rest of the pipeline actually relies on — interpolation
    /// at the knots, absence of overshoot, and flat extrapolation outside the range —
    /// plus the input contract, because a violation of it throws from inside
    /// FixedUpdate where an exception is expensive.
    /// </summary>
    public class PchipTests
    {
        private static float[] Lin(int n, float step = 1f)
        {
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = i * step;
            return x;
        }

        [Test]
        public void Evaluate_AtKnots_ReturnsSampleValuesExactly()
        {
            float[] x = Lin(6);
            float[] y = { 0f, 3f, 1f, 4f, 4f, 2f };
            var p = new Pchip(x, y);

            for (int i = 0; i < x.Length; i++)
                Assert.AreEqual(y[i], p.Evaluate(x[i]), 1e-5f,
                    $"PCHIP must interpolate, not approximate, at knot {i}");
        }

        [Test]
        public void Evaluate_MonotoneData_NeverOvershoots()
        {
            // The property the whole choice of interpolant rests on. A natural cubic
            // would swing past 10 between the last two knots; this must not.
            float[] x = Lin(5);
            float[] y = { 0f, 1f, 3f, 9f, 10f };
            var p = new Pchip(x, y);

            for (float t = x[0]; t <= x[x.Length - 1]; t += 0.01f)
            {
                float v = p.Evaluate(t);
                Assert.GreaterOrEqual(v, y[0] - 1e-4f, $"undershoot at t={t}");
                Assert.LessOrEqual(v, y[y.Length - 1] + 1e-4f, $"overshoot at t={t}");
            }
        }

        [Test]
        public void Evaluate_MonotoneData_IsItselfMonotone()
        {
            float[] x = Lin(6);
            float[] y = { 0f, 0.5f, 2f, 2.1f, 8f, 8.05f };
            var p = new Pchip(x, y);

            float prev = p.Evaluate(x[0]);
            for (float t = x[0]; t <= x[5]; t += 0.005f)
            {
                float v = p.Evaluate(t);
                Assert.GreaterOrEqual(v, prev - 1e-4f,
                    $"monotone input produced a decreasing interval at t={t}");
                prev = v;
            }
        }

        [Test]
        public void Evaluate_OutsideRange_ExtrapolatesFlat()
        {
            float[] x = Lin(4);
            float[] y = { 2f, 5f, 1f, 7f };
            var p = new Pchip(x, y);

            Assert.AreEqual(y[0], p.Evaluate(x[0] - 100f), 1e-6f);
            Assert.AreEqual(y[3], p.Evaluate(x[3] + 100f), 1e-6f);
        }

        [Test]
        public void Derivative_OutsideRange_IsZero()
        {
            float[] x = Lin(4);
            float[] y = { 2f, 5f, 1f, 7f };
            var p = new Pchip(x, y);

            Assert.AreEqual(0f, p.Derivative(x[0] - 1f), 1e-6f);
            Assert.AreEqual(0f, p.Derivative(x[3] + 1f), 1e-6f);
        }

        [Test]
        public void Derivative_AtAnInteriorExtremum_IsZero()
        {
            // y turns at index 2. Fritsch-Carlson sets the tangent to zero when the
            // adjacent secants disagree in sign, which is what ExtremaDetector hunts.
            float[] x = Lin(5);
            float[] y = { 0f, 2f, 4f, 2f, 0f };
            var p = new Pchip(x, y);

            Assert.AreEqual(0f, p.Derivative(x[2]), 1e-4f,
                "a knot with secants of opposite sign must carry a zero tangent");
        }

        [Test]
        public void Fit_FlatSegment_StaysExactlyFlat()
        {
            float[] x = Lin(4);
            float[] y = { 1f, 3f, 3f, 5f };
            var p = new Pchip(x, y);

            for (float t = x[1]; t <= x[2]; t += 0.02f)
                Assert.AreEqual(3f, p.Evaluate(t), 1e-4f,
                    "a constant segment must not bulge between its endpoints");
        }

        [Test]
        public void Fit_TwoSamples_IsTheSecant()
        {
            var p = new Pchip(new[] { 0f, 1f }, new[] { 0f, 4f });
            Assert.AreEqual(2f, p.Evaluate(0.5f), 1e-4f);
        }

        [Test]
        public void Fit_NonIncreasingX_Throws()
        {
            var p = new Pchip(8);
            Assert.Throws<ArgumentException>(
                () => p.Fit(new[] { 0f, 1f, 1f, 2f }, new[] { 0f, 1f, 2f, 3f }, 4),
                "duplicate timestamps must be rejected, not silently fitted");
            Assert.Throws<ArgumentException>(
                () => p.Fit(new[] { 0f, 2f, 1f, 3f }, new[] { 0f, 1f, 2f, 3f }, 4));
        }

        [Test]
        public void Fit_FewerThanTwoSamples_Throws()
        {
            var p = new Pchip(8);
            Assert.Throws<ArgumentException>(() => p.Fit(new[] { 0f }, new[] { 1f }, 1));
        }

        [Test]
        public void Fit_CountBeyondArrayLength_Throws()
        {
            var p = new Pchip(8);
            Assert.Throws<ArgumentException>(() => p.Fit(new[] { 0f, 1f }, new[] { 0f, 1f }, 5));
        }

        [Test]
        public void Fit_NullInput_Throws()
        {
            var p = new Pchip(8);
            Assert.Throws<ArgumentNullException>(() => p.Fit(null, new[] { 0f, 1f }, 2));
            Assert.Throws<ArgumentNullException>(() => p.Fit(new[] { 0f, 1f }, null, 2));
        }

        [Test]
        public void Fit_Repeated_ReusesStorageAndDoesNotAllocate()
        {
            // The live steppers refit four of these per bone per frame. If Fit allocates,
            // it dominates the frame's GC cost — this is the guard on that.
            var p = new Pchip(32);
            float[] x = Lin(30, 0.016f);
            var y = new float[30];
            for (int i = 0; i < 30; i++) y[i] = Mathf.Sin(i * 0.2f);

            p.Fit(x, y, 30);   // first call may allocate its backing arrays

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++) p.Fit(x, y, 30);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, bytes,
                $"Fit allocated {bytes} B across 200 refits; the live steppers run four of " +
                "these per bone per frame, so a refit that allocates dominates GC cost");
        }

        [Test]
        public void Fit_ShrinkingCount_DoesNotReadStaleTail()
        {
            var p = new Pchip(16);
            p.Fit(Lin(10), new[] { 0f, 9f, 1f, 8f, 2f, 7f, 3f, 6f, 4f, 5f }, 10);
            p.Fit(Lin(3), new[] { 0f, 1f, 2f }, 3);

            // TMax must follow the shorter fit, not the array capacity left behind.
            Assert.AreEqual(2f, p.TMax, 1e-6f);
            Assert.AreEqual(2f, p.Evaluate(50f), 1e-4f);
        }
    }
}
