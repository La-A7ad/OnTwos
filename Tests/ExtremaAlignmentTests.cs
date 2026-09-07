using System.Collections.Generic;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// The objective half of the experiment RESEARCH.md 6 asks for.
    ///
    /// The premise behind arc-length placement and extrema detection is that the system
    /// "spends drawings where the motion is" — that adaptive placement puts held poses on
    /// the extremes, the way an animator would, rather than on a metronome. That premise
    /// has never been measured. This measures it.
    ///
    /// <b>Question.</b> At a matched pose budget, do adaptive holds sit closer to true
    /// motion extrema than uniform fixed-rate holds do?
    ///
    /// <b>Ground truth is analytic, deliberately.</b> Extrema are found by solving the
    /// derivative of the synthetic motion's own angle function, NOT by running
    /// ExtremaDetector. Scoring the system with its own detector would be circular — it
    /// would measure whether the scheduler agrees with its detector, not whether either
    /// agrees with the motion. Swing rotates about a fixed axis, so the extrema of the
    /// scalar angle are exactly the extrema of the rotation.
    ///
    /// <b>Metric is pose distance, not time distance.</b> On the tau path the held pose
    /// comes from the candidate's time while the caller observes it at the tick's time, so
    /// a time-based score would penalise bookkeeping rather than placement. Pose distance
    /// is what a viewer sees and needs no such correction. Two directions are reported,
    /// because they answer different questions:
    ///   PRECISION — mean distance from each emitted pose to its nearest extremum.
    ///               "Is a typical drawing spent on an extreme?"
    ///   COVERAGE  — mean distance from each extremum to its nearest emitted pose.
    ///               "Is every extreme represented by some drawing?"
    /// Precision is punished by a generous budget; coverage is rewarded by it. Reporting
    /// only one would be picking the flattering direction.
    ///
    /// <b>The budget sweep is the whole point.</b> An earlier version of this test ran a
    /// single realistic cadence and produced 263-461 emitted poses against 12 true
    /// extrema. At roughly 30 drawings per extremum neither strategy CAN concentrate poses
    /// on extrema, both score at the chance line, and the comparison is vacuous. The
    /// premise is only testable where drawings are scarce relative to the motion's
    /// structure, so the budget is swept from far below one pose per extremum to far
    /// above it, and the poses-per-extremum ratio is reported next to every row.
    ///
    /// <b>What this cannot answer.</b> Whether the result LOOKS better. Landing on extrema
    /// is the stated premise, not established perceptual quality. Reported, never
    /// asserted.
    /// </summary>
    public class ExtremaAlignmentTests
    {
        private const float DurationSeconds = 8f;
        private const int CandidatesPerSegment = 2;
        private const int BufferSize = 30;
        private const float Fps = 60f;

        /// <summary>
        /// A motion defined by its angle function and that function's ANALYTIC derivative,
        /// so ground-truth extrema never depend on anything in Runtime.
        ///
        /// Two are used. SPARSE is SyntheticMotion.Swing, the motion the rest of the suite
        /// drives; its extrema are far apart. DENSE has the same shape at much higher
        /// frequency, which is the only way to reach a scarce pose budget at a cadence a
        /// viewer would accept — on SPARSE, adaptive cannot be driven below roughly three
        /// poses per extremum however low StepRate goes, because tau keeps firing.
        /// Without DENSE this test would only ever observe the saturated regime.
        /// </summary>
        private readonly struct Motion
        {
            public readonly string Name;
            public readonly System.Func<float, float> Angle;
            public readonly System.Func<float, float> AngleDot;

            public Motion(string name, System.Func<float, float> a, System.Func<float, float> d)
            { Name = name; Angle = a; AngleDot = d; }

            public Quaternion Pose(float t) =>
                SyntheticMotion.AboutAxis(new Vector3(0f, 0f, 1f), Angle(t));
        }

        // SPARSE: a(t) = 40 sin(3.1t) + 12 sin(7.9t)  — identical to SyntheticMotion.Swing
        //         a'(t) = 124 cos(3.1t) + 94.8 cos(7.9t)
        // DENSE:  a(t) = 25 sin(11t) + 10 sin(23t)
        //         a'(t) = 275 cos(11t) + 230 cos(23t)
        private static readonly Motion[] Motions =
        {
            new Motion("SPARSE",
                t => 40f * Mathf.Sin(3.1f * t) + 12f * Mathf.Sin(7.9f * t),
                t => 124f * Mathf.Cos(3.1f * t) + 94.8f * Mathf.Cos(7.9f * t)),
            new Motion("DENSE",
                t => 25f * Mathf.Sin(11f * t) + 10f * Mathf.Sin(23f * t),
                t => 275f * Mathf.Cos(11f * t) + 230f * Mathf.Cos(23f * t)),
        };

        private static List<float> TrueExtrema(Motion m, float t0, float t1)
        {
            var roots = new List<float>();
            const float scan = 0.0005f;

            float prev = m.AngleDot(t0);
            for (float t = t0 + scan; t <= t1; t += scan)
            {
                float curr = m.AngleDot(t);
                if (prev * curr < 0f)
                {
                    float lo = t - scan, hi = t;
                    for (int i = 0; i < 60; i++)
                    {
                        float mid = 0.5f * (lo + hi);
                        if (m.AngleDot(lo) * m.AngleDot(mid) <= 0f) hi = mid;
                        else lo = mid;
                    }
                    roots.Add(0.5f * (lo + hi));
                }
                prev = curr;
            }
            return roots;
        }

        private static List<Quaternion> Drive(Motion m, float stepRate, float jitter, float tau)
        {
            var s = new HoldFrameScheduler(tau, CandidatesPerSegment, BufferSize);
            s.MaxHoldSeconds = 1f / stepRate;
            s.MinHoldSeconds = s.MaxHoldSeconds * (1f - jitter);

            var emitted = new List<Quaternion>();
            float dt = 1f / Fps;
            int ticks = Mathf.RoundToInt(DurationSeconds * Fps);

            for (int i = 0; i < ticks; i++)
            {
                float t = i * dt;
                Quaternion held = s.Update(t, m.Pose(t));
                if (s.DidSnap) emitted.Add(held);
            }
            return emitted;
        }

        /// <summary>Mean over A of the nearest angular distance to any member of B.</summary>
        private static float MeanNearest(IReadOnlyList<Quaternion> a, IReadOnlyList<Quaternion> b)
        {
            if (a.Count == 0 || b.Count == 0) return float.NaN;
            double sum = 0;
            for (int i = 0; i < a.Count; i++)
            {
                float best = float.MaxValue;
                for (int j = 0; j < b.Count; j++)
                {
                    float d = Quaternion.Angle(a[i], b[j]);
                    if (d < best) best = d;
                }
                sum += best;
            }
            return (float)(sum / a.Count);
        }

        [Test]
        public void Report_ExtremaAlignment_BudgetSweep()
        {
            const float tau = 5f;
            var sb = new System.Text.StringBuilder();

            foreach (Motion m in Motions)
            foreach (float jitter in new[] { 0.5f, 1.0f })
            {
                List<float> extremaTimes = TrueExtrema(m, 0f, DurationSeconds);
                var extremaPoses = new List<Quaternion>(extremaTimes.Count);
                foreach (float t in extremaTimes) extremaPoses.Add(m.Pose(t));

                Assert.Greater(extremaTimes.Count, 8,
                    $"{m.Name} must contain enough turning points for this to mean anything; " +
                    "if this fails, the motion or its analytic derivative changed");

                sb.AppendLine();
                sb.AppendLine($"=== {m.Name}: {extremaTimes.Count} true extrema in {DurationSeconds}s " +
                              $"| CadenceJitter={jitter}, tau={tau} deg ===");
                sb.AppendLine("            adaptive             |   matched fixed-rate      | delta (a - f)");
                sb.AppendLine("rate poses  p/ext  prec   cover  | rate   poses prec   cover | prec   cover");
                sb.AppendLine("---------------------------------+---------------------------+--------------");

                foreach (float stepRate in new[] { 1.5f, 2f, 3f, 4f, 6f, 8f, 12f, 18f, 24f })
                {
                    List<Quaternion> adaptive = Drive(m, stepRate, jitter, tau);
                    if (adaptive.Count == 0) continue;

                    float aPrec  = MeanNearest(adaptive, extremaPoses);
                    float aCover = MeanNearest(extremaPoses, adaptive);

                    // Matched pose budget: same number of drawings, spent uniformly.
                    float matchedRate = adaptive.Count / DurationSeconds;
                    List<Quaternion> locked = Drive(m, matchedRate, 0f, tau);
                    float lPrec  = MeanNearest(locked, extremaPoses);
                    float lCover = MeanNearest(extremaPoses, locked);

                    sb.AppendLine(
                        $"{stepRate,4:F1} {adaptive.Count,5} " +
                        $"{adaptive.Count / (float)extremaTimes.Count,6:F1} {aPrec,6:F2} {aCover,6:F2}  | " +
                        $"{matchedRate,5:F2} {locked.Count,5} {lPrec,6:F2} {lCover,6:F2} | " +
                        $"{aPrec - lPrec,+6:F2} {aCover - lCover,+6:F2}");
                }
            }

            Debug.Log(
                "EXTREMA ALIGNMENT — budget sweep, two motion densities\n" +
                "p/ext  = emitted poses per true extremum; the premise is only testable near 1\n" +
                "prec   = mean deg from an emitted pose to its nearest extremum (lower = better)\n" +
                "cover  = mean deg from an extremum to its nearest emitted pose (lower = better)\n" +
                "delta  = adaptive minus fixed-rate; NEGATIVE means adaptive is better\n" + sb);
        }
    }
}
