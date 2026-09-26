using System;
using System.Collections.Generic;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Brent's method is published and not re-derived here. What is tested is the
    /// contract the scheduler depends on: results cleared and sorted, turning points
    /// actually found where the motion has them, near-coincident roots merged across
    /// all four components, and no allocation on the steady-state path.
    ///
    /// Ground truth is the analytic derivative of the driving angle function, never the
    /// detector's own output.
    /// </summary>
    public class ExtremaDetectorTests
    {
        private const float Dt = 1f / 60f;

        // a(t) = 30 sin(2 pi f t)  ->  extrema at t = (2k+1) / (4f)
        private static MonotoneCubicSampler DrivenSine(float freq, float duration, int capacity = 128)
        {
            var s = new MonotoneCubicSampler(capacity);
            int n = Mathf.RoundToInt(duration / Dt);
            for (int i = 0; i <= n; i++)
            {
                float t = i * Dt;
                s.Add(t, SyntheticMotion.AboutAxis(Vector3.forward,
                                                   30f * Mathf.Sin(2f * Mathf.PI * freq * t)));
            }
            return s;
        }

        private static List<float> AnalyticExtrema(float freq, float t0, float t1)
        {
            var e = new List<float>();
            for (int k = 0; ; k++)
            {
                float t = (2 * k + 1) / (4f * freq);
                if (t > t1) break;
                if (t >= t0) e.Add(t);
            }
            return e;
        }

        [Test]
        public void FindForBone_NullArguments_Throw()
        {
            Assert.Throws<ArgumentNullException>(
                () => ExtremaDetector.FindForBone(null, 0f, 1f, new List<float>()));
            Assert.Throws<ArgumentNullException>(
                () => ExtremaDetector.FindForBone(new MonotoneCubicSampler(30), 0f, 1f, null));
        }

        [Test]
        public void FindForBone_ClearsResultsEvenWhenItFindsNothing()
        {
            var results = new List<float> { 99f, 98f };
            var s = new MonotoneCubicSampler(30);   // empty: derivative is zero everywhere

            ExtremaDetector.FindForBone(s, 0f, 1f, results);

            Assert.IsEmpty(results, "stale results from a previous window must not survive");
        }

        [Test]
        public void FindForBone_InvertedOrEmptyWindow_ReturnsNothing()
        {
            var s = DrivenSine(2f, 1.5f);
            var results = new List<float> { 42f };

            ExtremaDetector.FindForBone(s, 1f, 1f, results);
            Assert.IsEmpty(results);

            results.Add(42f);
            ExtremaDetector.FindForBone(s, 1f, 0.5f, results);
            Assert.IsEmpty(results);
        }

        [Test]
        public void FindForBone_ReturnsResultsInAscendingOrder()
        {
            var s = DrivenSine(2f, 2f);
            var results = new List<float>();

            ExtremaDetector.FindForBone(s, 0.05f, 1.9f, results);

            Assert.Greater(results.Count, 1, "this motion has several turning points");
            for (int i = 1; i < results.Count; i++)
                Assert.Greater(results[i], results[i - 1], "the merged list must be sorted");
        }

        [Test]
        public void FindForBone_ResultsLieInsideTheRequestedWindow()
        {
            var s = DrivenSine(2f, 2f);
            var results = new List<float>();
            const float t0 = 0.3f, t1 = 1.4f;

            ExtremaDetector.FindForBone(s, t0, t1, results);

            foreach (float e in results)
            {
                Assert.GreaterOrEqual(e, t0 - 1e-3f);
                Assert.LessOrEqual(e, t1 + 1e-3f);
            }
        }

        [Test]
        public void FindForBone_FindsTheTurningPointsTheMotionActuallyHas()
        {
            const float freq = 1.5f;
            var s = DrivenSine(freq, 2.5f);
            var results = new List<float>();
            const float t0 = 0.1f, t1 = 2.3f;

            ExtremaDetector.FindForBone(s, t0, t1, results);

            List<float> expected = AnalyticExtrema(freq, t0 + 0.05f, t1 - 0.05f);
            Assert.Greater(expected.Count, 1, "test setup must contain turning points");

            foreach (float want in expected)
            {
                bool found = false;
                foreach (float got in results)
                    if (Mathf.Abs(got - want) <= 2f * Dt) { found = true; break; }

                Assert.IsTrue(found,
                    $"no detected extremum within two frames of the analytic turning point at {want:F4}. " +
                    $"detected: [{string.Join(", ", results.ConvertAll(x => x.ToString("F4")))}]");
            }
        }

        [Test]
        public void FindForBone_DoesNotReportExtremaOnMonotoneMotion()
        {
            // A steadily turning bone has no turning point. Anything reported here is a
            // phantom, and the scheduler would spend a pose on it.
            var s = new MonotoneCubicSampler(64);
            for (int i = 0; i <= 60; i++)
                s.Add(i * Dt, SyntheticMotion.AboutAxis(Vector3.forward, 2f * i));

            var results = new List<float>();
            ExtremaDetector.FindForBone(s, 0.1f, 0.9f, results);

            Assert.IsEmpty(results, $"phantom extrema on monotone input: " +
                $"[{string.Join(", ", results.ConvertAll(x => x.ToString("F4")))}]");
        }

        [Test]
        public void FindForBone_MergesRootsCloserThanOneFrame()
        {
            var s = DrivenSine(2f, 2f);
            var results = new List<float>();

            ExtremaDetector.FindForBone(s, 0.05f, 1.9f, results);

            for (int i = 1; i < results.Count; i++)
                Assert.GreaterOrEqual(results[i] - results[i - 1], 0.016f - 1e-5f,
                    "two components crossing zero within a frame describe one turning point");
        }

        [Test]
        public void FindForBone_StationaryInput_ReportsNothing()
        {
            var s = new MonotoneCubicSampler(30);
            Quaternion still = SyntheticMotion.AboutAxis(Vector3.up, 12f);
            for (int i = 0; i < 25; i++) s.Add(i * Dt, still);

            var results = new List<float>();
            Assert.DoesNotThrow(() => ExtremaDetector.FindForBone(s, 0.05f, 0.35f, results));
            Assert.IsEmpty(results, "a flat derivative never crosses zero, it sits on it");
        }

        [Test]
        public void FindForBone_HemisphereInconsistentInput_DoesNotInventExtrema()
        {
            // Every third sample negated is the same physical motion. If sign
            // normalisation were skipped the fit would spike and this would report far
            // more turning points than the motion has.
            var consistent = new MonotoneCubicSampler(128);
            var flipped    = new MonotoneCubicSampler(128);
            for (int i = 0; i <= 120; i++)
            {
                float t = i * Dt;
                consistent.Add(t, SyntheticMotion.Swing(t));
                flipped.Add(t, SyntheticMotion.HemisphereInconsistentSwing(t, i));
            }

            var a = new List<float>();
            var b = new List<float>();
            ExtremaDetector.FindForBone(consistent, 0.1f, 1.9f, a);
            ExtremaDetector.FindForBone(flipped,    0.1f, 1.9f, b);

            Assert.AreEqual(a.Count, b.Count,
                "sign normalisation must make the two streams indistinguishable");
        }

        [Test]
        public void FindForBone_RepeatedCalls_DoNotAllocate()
        {
            var s = DrivenSine(2f, 2f);
            var results = new List<float>(64);
            ExtremaDetector.FindForBone(s, 0.05f, 0.55f, results);   // warm the scratch list

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 300; i++)
                ExtremaDetector.FindForBone(s, 0.05f, 0.55f, results);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(0, bytes,
                $"detection allocated {bytes} B across 300 scans; the ThreadStatic scratch " +
                "and the caller's list must both be reused");
        }
    }
}
