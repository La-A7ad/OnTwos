using System;
using System.Collections.Generic;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// The scheduler's behavioural contract, as distinct from its cadence parity
    /// (LockedParityTests) and its output quality (AdaptiveDeltaTests).
    ///
    /// Everything here is about invariants that hold regardless of configuration: a
    /// snap is reported when and only when one happened, a held pose does not change
    /// without a snap, the hold clock only ever moves forward, and MinHoldSeconds means
    /// what it says.
    ///
    /// The three Adaptive_* cadence tests are regression guards for a defect fixed on
    /// 2026-09-26: the tau walk stamped the hold clock with a candidate time that could
    /// precede the previous snap, so the clock ran backwards and a step could land inside
    /// the MinHoldSeconds guard. They fail if that behaviour returns.
    /// </summary>
    public class SchedulerContractTests
    {
        private const float Fps = 60f;
        private const float Dt  = 1f / Fps;

        private sealed class Run
        {
            public readonly List<float> SnapTimes = new List<float>();
            public readonly List<float> LastSnapTimeAfterEachTick = new List<float>();
            public readonly List<Quaternion> Output = new List<Quaternion>();
        }

        private static Run Drive(
            HoldFrameScheduler s, Func<float, Quaternion> motion, float duration,
            bool probe = false)
        {
            var run = new Run();
            int ticks = Mathf.RoundToInt(duration * Fps);
            for (int i = 0; i < ticks; i++)
            {
                float t = i * Dt;
                run.Output.Add(s.Update(t, motion(t)));
                if (s.DidSnap) run.SnapTimes.Add(t);
                if (probe) run.LastSnapTimeAfterEachTick.Add(SchedulerProbe.GetLastSnapTime(s));
            }
            return run;
        }

        private static HoldFrameScheduler Adaptive(float stepRate, float jitter, float tau = 5f)
        {
            var s = new HoldFrameScheduler(tau, 2, 30);
            s.MaxHoldSeconds = 1f / stepRate;
            s.MinHoldSeconds = s.MaxHoldSeconds * (1f - jitter);
            return s;
        }

        // ------------------------------------------------------------------ construction

        [Test]
        public void Constructor_CandidatesOutOfRange_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldFrameScheduler(5f, 0, 30));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HoldFrameScheduler(5f, 5, 30));
        }

        [Test]
        public void CandidatesPerSegment_ClampsRatherThanThrowing()
        {
            // The steppers compute this from a ResponseCurve every frame, so a curve
            // that spikes must not be able to throw out of the per-bone loop.
            var s = new HoldFrameScheduler(5f, 2, 30);
            s.CandidatesPerSegment = 99;
            Assert.AreEqual(4, s.CandidatesPerSegment);
            s.CandidatesPerSegment = -7;
            Assert.AreEqual(1, s.CandidatesPerSegment);
        }

        // ------------------------------------------------------------------ DidSnap

        [Test]
        public void DidSnap_IsFalseOnTheSeedFrame()
        {
            var s = Adaptive(12f, 0f);
            s.Update(0f, SyntheticMotion.Swing(0f));
            Assert.IsFalse(s.DidSnap, "seeding the window is not a step");
        }

        [Test]
        public void DidSnap_IsFalseThroughWarmUp()
        {
            var s = Adaptive(12f, 0f);
            for (int i = 0; i < 4; i++)
            {
                s.Update(i * Dt, SyntheticMotion.Swing(i * Dt));
                Assert.IsFalse(s.DidSnap,
                    $"tick {i} is still tracking continuously, which is the opposite of a hold");
            }
        }

        [Test]
        public void DidSnap_IsTrueExactlyWhenTheHeldPoseChanges()
        {
            var s = Adaptive(12f, 0.5f);
            Quaternion previous = s.Update(0f, SyntheticMotion.Swing(0f));

            for (int i = 1; i < 600; i++)
            {
                float t = i * Dt;
                Quaternion held = s.Update(t, SyntheticMotion.Swing(t));
                bool changed = !SyntheticMotion.BitwiseEquals(previous, held);

                if (changed)
                    Assert.IsTrue(s.DidSnap, $"pose changed at t={t:F4} without reporting a snap");

                previous = held;
            }
        }

        [Test]
        public void HeldPose_IsConstantBetweenSnaps()
        {
            var s = Adaptive(12f, 0f);
            Quaternion previous = Quaternion.identity;
            bool have = false;

            for (int i = 0; i < 600; i++)
            {
                float t = i * Dt;
                Quaternion held = s.Update(t, SyntheticMotion.Swing(t));
                if (have && !s.DidSnap)
                    Assert.IsTrue(SyntheticMotion.BitwiseEquals(previous, held),
                        $"held pose drifted at t={t:F4} without a snap");
                previous = held;
                have = true;
            }
        }

        // ------------------------------------------------------------------ Reset

        [Test]
        public void Reset_ClearsDidSnapAndReseedsFromTheNextTimestamp()
        {
            var s = Adaptive(12f, 0f);
            Drive(s, SyntheticMotion.Swing, 2f);

            s.Reset(SyntheticMotion.AboutAxis(Vector3.up, 42f));
            Assert.IsFalse(s.DidSnap, "Reset must not leave a stale snap flag");

            Quaternion seeded = s.Update(100f, SyntheticMotion.AboutAxis(Vector3.up, 7f));
            Assert.IsFalse(s.DidSnap, "the first tick after Reset is a seed, not a snap");
            Assert.IsTrue(SyntheticMotion.BitwiseEquals(
                SyntheticMotion.AboutAxis(Vector3.up, 7f), seeded),
                "the seed frame tracks its input verbatim");
        }

        [Test]
        public void Reset_DiscardsThePreResetSampleWindow()
        {
            // Without this the PCHIP window fits across the discontinuity and the first
            // stepped frames after a state change ghost the old pose.
            var s = Adaptive(12f, 1f, tau: 3f);
            Drive(s, t => SyntheticMotion.AboutAxis(Vector3.forward, 80f * Mathf.Sin(t * 9f)), 2f);

            s.Reset(SyntheticMotion.AboutAxis(Vector3.forward, 0f));

            var run = Drive(s, _ => SyntheticMotion.AboutAxis(Vector3.forward, 0f), 0.5f);
            foreach (Quaternion q in run.Output)
                Assert.LessOrEqual(
                    Quaternion.Angle(SyntheticMotion.AboutAxis(Vector3.forward, 0f), q), 1e-2f,
                    "motion from before the Reset bled into the new window");
        }

        [Test]
        public void Reset_RephasesSchedulersOntoOneBeat()
        {
            // What puts a whole rig in step: every scheduler reset together snaps together.
            var a = Adaptive(12f, 0f);
            var b = Adaptive(12f, 0f);

            for (int i = 0; i < 37; i++) a.Update(i * Dt, SyntheticMotion.Swing(i * Dt));

            a.Reset(Quaternion.identity);
            b.Reset(Quaternion.identity);

            var aSnaps = new List<float>();
            var bSnaps = new List<float>();
            for (int i = 0; i < 300; i++)
            {
                float t = 10f + i * Dt;
                a.Update(t, SyntheticMotion.Swing(t)); if (a.DidSnap) aSnaps.Add(t);
                b.Update(t, SyntheticMotion.Swing(t)); if (b.DidSnap) bSnaps.Add(t);
            }

            CollectionAssert.AreEqual(aSnaps, bSnaps,
                "two schedulers reset on the same tick must stay in phase");
        }

        // ------------------------------------------------------------------ degenerate input

        [Test]
        public void RepeatedTimestamp_LeavesTheHeldPoseUntouched()
        {
            // timeScale = 0 feeds LateUpdate the same Time.time every frame.
            var s = Adaptive(12f, 0f);
            Drive(s, SyntheticMotion.Swing, 1f);

            Quaternion before = s.Update(1f, SyntheticMotion.Swing(1f));
            for (int i = 0; i < 50; i++)
            {
                Quaternion held = s.Update(1f, SyntheticMotion.Swing(2f));
                Assert.IsTrue(SyntheticMotion.BitwiseEquals(before, held),
                    "a zero-length interval must not advance the hold");
                Assert.IsFalse(s.DidSnap);
            }
        }

        [Test]
        public void BackwardsTimestamp_DoesNotThrowOrEmitNaN()
        {
            var s = Adaptive(12f, 0.5f);
            Drive(s, SyntheticMotion.Swing, 1f);

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 30; i++)
                {
                    Quaternion held = s.Update(1f - i * Dt, SyntheticMotion.Swing(1f));
                    Assert.IsFalse(float.IsNaN(held.x), "produced a NaN pose on a rewound clock");
                }
            });
        }

        [Test]
        public void LargeTimeJump_ProducesOneSnapNotABurst()
        {
            // A breakpoint, a level load or a long frame hitch must not back-date a
            // stream of snaps to catch the grid up.
            var s = Adaptive(12f, 0f);
            Drive(s, SyntheticMotion.Swing, 1f);

            int snapsAcrossTheJump = 0;
            for (int i = 0; i < 5; i++)
            {
                float t = 30f + i * Dt;
                s.Update(t, SyntheticMotion.Swing(t));
                if (s.DidSnap) snapsAcrossTheJump++;
            }

            Assert.LessOrEqual(snapsAcrossTheJump, 2,
                "a 29-second stall produced a burst of catch-up snaps");
        }

        [Test]
        public void ConfigurationChangeMidRun_DoesNotThrow()
        {
            // Profile sliders are pushed onto live schedulers every frame.
            var s = Adaptive(12f, 0f);
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 600; i++)
                {
                    float t = i * Dt;
                    if (i == 200) { s.MinHoldSeconds = s.MaxHoldSeconds * 0.5f; }   // locked -> adaptive
                    if (i == 400) { s.MinHoldSeconds = s.MaxHoldSeconds; }          // adaptive -> locked
                    s.Tau = 3f + (i % 17);
                    s.Update(t, SyntheticMotion.Swing(t));
                }
            });
        }

        // ------------------------------------------------------------------ cadence invariants

        [Test]
        public void Locked_SnapsAreSpacedByTheStepInterval()
        {
            var s = Adaptive(12f, 0f);
            Run run = Drive(s, SyntheticMotion.Swing, 5f);

            Assert.Greater(run.SnapTimes.Count, 40);
            float expected = 1f / 12f;
            for (int i = 1; i < run.SnapTimes.Count; i++)
            {
                float gap = run.SnapTimes[i] - run.SnapTimes[i - 1];
                Assert.AreEqual(expected, gap, Dt + 1e-4f,
                    $"locked cadence drifted at snap {i}");
            }
        }

        [Test]
        public void Adaptive_SnapsAreNeverCloserThanMinHoldSeconds()
        {
            // MinHoldSeconds exists to stop a fast bone stepping sub-frame. Regression
            // guard: the tau walk used to stamp _lastSnapTime with the winning CANDIDATE's
            // time, which could be a whole sample window (~0.5 s) older than the previous
            // snap, inflating the next tick's heldFor and firing a snap early.
            var s = Adaptive(12f, 0.5f, tau: 4f);
            Run run = Drive(s, SyntheticMotion.Swing, 12f);

            Assert.Greater(run.SnapTimes.Count, 10, "test needs snaps to inspect");

            float min = s.MinHoldSeconds - Dt - 1e-4f;
            for (int i = 1; i < run.SnapTimes.Count; i++)
            {
                float gap = run.SnapTimes[i] - run.SnapTimes[i - 1];
                Assert.GreaterOrEqual(gap, min,
                    $"snap {i} landed {gap:F4}s after the previous one, " +
                    $"inside MinHoldSeconds={s.MinHoldSeconds:F4}");
            }
        }

        [Test]
        public void Adaptive_TheHoldClockNeverMovesBackwards()
        {
            // The direct form of the same regression, read off the private field so a
            // failure points at the cause rather than at a downstream symptom.
            var s = Adaptive(12f, 0.5f, tau: 4f);
            Run run = Drive(s, SyntheticMotion.Swing, 12f, probe: true);

            for (int i = 1; i < run.LastSnapTimeAfterEachTick.Count; i++)
            {
                float prev = run.LastSnapTimeAfterEachTick[i - 1];
                float curr = run.LastSnapTimeAfterEachTick[i];
                Assert.GreaterOrEqual(curr, prev - 1e-5f,
                    $"_lastSnapTime went backwards at tick {i}: {prev:F4} -> {curr:F4} " +
                    $"(a jump of {prev - curr:F4}s into the past)");
            }
        }

        [Test]
        public void Adaptive_NeverHoldsAPoseOlderThanTheWindow()
        {
            // The visual consequence of the same regression: the walk keeps the last
            // candidate that exceeded tau, and on a reversing motion that could be one
            // from the far end of the buffer, so the proxy showed a pose from half a
            // second ago. Candidates at or before the last snap are now skipped.
            var s = Adaptive(12f, 1f, tau: 4f);

            for (int i = 0; i < 700; i++)
            {
                float t = i * Dt;
                Quaternion held = s.Update(t, SyntheticMotion.Swing(t));
                if (!s.DidSnap || t < 1f) continue;

                float bestAgeSeconds = float.MaxValue;
                for (int back = 0; back <= 30; back++)
                {
                    float sampleT = t - back * Dt;
                    if (Quaternion.Angle(SyntheticMotion.Swing(sampleT), held) < 1.5f)
                    { bestAgeSeconds = back * Dt; break; }
                }

                Assert.Less(bestAgeSeconds, 0.25f,
                    $"at t={t:F4} the scheduler emitted a pose that best matches the source " +
                    $"{bestAgeSeconds:F3}s in the past");
            }
        }

        [Test]
        public void HighTau_ProducesOnlyForcedSnaps()
        {
            var s = Adaptive(12f, 0.5f, tau: 100000f);
            Run run = Drive(s, SyntheticMotion.Swing, 5f);

            float expected = 1f / 12f;
            for (int i = 1; i < run.SnapTimes.Count; i++)
                Assert.AreEqual(expected, run.SnapTimes[i] - run.SnapTimes[i - 1], Dt + 1e-4f,
                    "with tau unreachable, only MaxHoldSeconds should fire");
        }

        [Test]
        public void InfiniteMaxHold_NeverForcesASnap()
        {
            var s = new HoldFrameScheduler(5f, 2, 30)
            {
                MinHoldSeconds = 0f,
                MaxHoldSeconds = float.PositiveInfinity
            };

            Run run = Drive(s, _ => SyntheticMotion.AboutAxis(Vector3.up, 10f), 5f);

            Assert.IsEmpty(run.SnapTimes,
                "a stationary bone with no forced cadence must never step");
        }

    }
}
