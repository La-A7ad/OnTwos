using System.Collections.Generic;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Characterises locked-cadence output (CadenceJitter = 0).
    ///
    /// These assertions are deliberately behavioural rather than a mirror of the
    /// implementation. Reimplementing AdvanceSnapGrid in the test would only prove the
    /// test agrees with itself. Instead they state the three properties that make the
    /// "locked cadence is uniform Nth-frame resampling" claim true:
    ///
    ///   1. every emitted pose is bitwise one of the raw input samples — never an
    ///      interpolated value off the spline;
    ///   2. snaps land on a fixed time grid of 1/StepRate, quantised up to the caller's
    ///      tick, with no drift accumulating over the run;
    ///   3. between snaps the output does not move at all — a true hold.
    ///
    /// Together those also serve as the regression guard required before any change to
    /// candidate placement: such a change must leave locked output untouched.
    /// </summary>
    public class LockedParityTests
    {
        private const float Tau = 5f;
        private const int CandidatesPerSegment = 2;
        private const int BufferSize = 30;
        private const float DurationSeconds = 8f;

        private static readonly float[] StepRates = { 6f, 8f, 12f, 24f };
        private static readonly float[] FrameRates = { 30f, 50f, 60f, 90f, 144f };

        public struct Run
        {
            public List<Quaternion> Output;
            public List<Quaternion> RawInput;
            public List<float> SnapTimes;
            public List<float> Times;
        }

        /// <summary>Drive one scheduler over a synthetic swing and record everything.</summary>
        public static Run Drive(float stepRate, float frameRate, float cadenceJitter)
        {
            var s = new HoldFrameScheduler(Tau, CandidatesPerSegment, BufferSize);
            s.MaxHoldSeconds = 1f / stepRate;
            s.MinHoldSeconds = s.MaxHoldSeconds * (1f - cadenceJitter);

            var run = new Run
            {
                Output = new List<Quaternion>(),
                RawInput = new List<Quaternion>(),
                SnapTimes = new List<float>(),
                Times = new List<float>()
            };

            float dt = 1f / frameRate;
            int ticks = Mathf.RoundToInt(DurationSeconds * frameRate);

            for (int i = 0; i < ticks; i++)
            {
                float t = i * dt;
                Quaternion raw = SyntheticMotion.Swing(t);
                Quaternion held = s.Update(t, raw);

                run.Times.Add(t);
                run.RawInput.Add(raw);
                run.Output.Add(held);
                if (s.DidSnap) run.SnapTimes.Add(t);
            }

            return run;
        }

        [Test]
        public void Locked_EveryEmittedPoseIsARawInputSample(
            [ValueSource(nameof(StepRates))] float stepRate,
            [ValueSource(nameof(FrameRates))] float frameRate)
        {
            Run run = Drive(stepRate, frameRate, cadenceJitter: 0f);

            // A set of the raw bit patterns seen so far. The held pose may lag, so a
            // match against ANY earlier raw sample is the correct check.
            var seen = new HashSet<(int, int, int, int)>();

            for (int i = 0; i < run.Output.Count; i++)
            {
                Quaternion raw = run.RawInput[i];
                seen.Add(Key(raw));

                Assert.IsTrue(seen.Contains(Key(run.Output[i])),
                    $"tick {i} (t={run.Times[i]:F4}) emitted a pose that was never an input " +
                    $"sample — locked cadence produced an interpolated value. " +
                    $"StepRate={stepRate}, fps={frameRate}");
            }
        }

        [Test]
        public void Locked_SnapsLandOnAFixedGridWithoutDrift(
            [ValueSource(nameof(StepRates))] float stepRate,
            [ValueSource(nameof(FrameRates))] float frameRate)
        {
            Run run = Drive(stepRate, frameRate, cadenceJitter: 0f);

            float interval = 1f / stepRate;
            float frameInterval = 1f / frameRate;

            Assert.Greater(run.SnapTimes.Count, 2,
                $"expected repeated snaps at StepRate={stepRate}, fps={frameRate}");

            // The property that matters is absence of CUMULATIVE drift, not uniformity of
            // each individual gap. Snap times are quantised to the caller's tick, so an
            // individual gap may run one tick long; AdvanceSnapGrid then advances by whole
            // intervals, so the following gap comes back one tick short. Asserting on a
            // single gap would forbid that self-correction, which is the intended design.
            //
            // Instead: every snap must stay within one tick of the ideal grid position
            // measured from the first snap. That is exactly what TECHNICAL.md §4.6 claims
            // ("the beat stays locked to a fixed grid instead of drifting forward by the
            // frame overshoot each step").
            float origin = run.SnapTimes[0];
            float worst = 0f;
            int worstIndex = 0;

            for (int i = 1; i < run.SnapTimes.Count; i++)
            {
                float ideal = origin + i * interval;
                float error = Mathf.Abs(run.SnapTimes[i] - ideal);
                if (error > worst) { worst = error; worstIndex = i; }
            }

            Assert.LessOrEqual(worst, frameInterval + 1e-4f,
                $"beat drifted off the grid: snap {worstIndex} is {worst:F5}s from its ideal " +
                $"position, more than one tick ({frameInterval:F5}s). " +
                $"StepRate={stepRate}, fps={frameRate}");
        }

        [Test]
        public void Locked_OutputIsConstantBetweenSnaps(
            [ValueSource(nameof(StepRates))] float stepRate,
            [ValueSource(nameof(FrameRates))] float frameRate)
        {
            Run run = Drive(stepRate, frameRate, cadenceJitter: 0f);
            var snapSet = new HashSet<float>(run.SnapTimes);

            // Skip the warm-up. Before the sampler holds MinSamples entries the scheduler
            // assigns the incoming pose directly every tick (HoldFrameScheduler.cs:183-187),
            // which its own comment describes as continuous tracking rather than a hold.
            // The constancy property only applies once stepping has actually begun.
            int firstHeld = run.SnapTimes.Count > 0
                ? run.Times.IndexOf(run.SnapTimes[0])
                : 0;

            for (int i = firstHeld + 1; i < run.Output.Count; i++)
            {
                if (snapSet.Contains(run.Times[i])) continue;

                Assert.IsTrue(
                    SyntheticMotion.BitwiseEquals(run.Output[i], run.Output[i - 1]),
                    $"tick {i} (t={run.Times[i]:F4}) moved without a snap — not a true hold. " +
                    $"StepRate={stepRate}, fps={frameRate}");
            }
        }

        [Test]
        public void Locked_IsDeterministic(
            [ValueSource(nameof(StepRates))] float stepRate,
            [ValueSource(nameof(FrameRates))] float frameRate)
        {
            Run a = Drive(stepRate, frameRate, cadenceJitter: 0f);
            Run b = Drive(stepRate, frameRate, cadenceJitter: 0f);

            Assert.AreEqual(a.Output.Count, b.Output.Count);
            for (int i = 0; i < a.Output.Count; i++)
                Assert.IsTrue(SyntheticMotion.BitwiseEquals(a.Output[i], b.Output[i]),
                    $"non-deterministic at tick {i} (StepRate={stepRate}, fps={frameRate})");
        }

        /// <summary>
        /// Dumps the snap-gap sequence, expressed in caller ticks, for every configuration.
        /// A locked beat should show one repeating gap (or an alternation between two
        /// adjacent tick counts where the step interval is not a whole number of ticks).
        /// Anything else means the grid is slipping. Reported, not asserted.
        /// </summary>
        [Test]
        public void Locked_ReportSnapGapDistribution()
        {
            var sb = new System.Text.StringBuilder();

            foreach (float stepRate in StepRates)
            foreach (float frameRate in FrameRates)
            {
                Run run = Drive(stepRate, frameRate, cadenceJitter: 0f);
                float frameInterval = 1f / frameRate;
                float idealTicks = (1f / stepRate) / frameInterval;

                var histogram = new SortedDictionary<int, int>();
                for (int i = 1; i < run.SnapTimes.Count; i++)
                {
                    int ticks = Mathf.RoundToInt(
                        (run.SnapTimes[i] - run.SnapTimes[i - 1]) / frameInterval);
                    histogram.TryGetValue(ticks, out int c);
                    histogram[ticks] = c + 1;
                }

                var parts = new List<string>();
                foreach (var kv in histogram) parts.Add($"{kv.Key}x{kv.Value}");

                sb.AppendLine($"StepRate={stepRate,-5} fps={frameRate,-5} " +
                              $"ideal={idealTicks:F3} ticks/step  gaps: {string.Join(" ", parts)}");
            }

            Debug.Log("LOCKED SNAP GAP DISTRIBUTION (ticks between snaps)\n" + sb);
        }

        /// <summary>
        /// Emits a stable signature of locked output for every configuration in the grid.
        /// Not an assertion — this is the baseline artefact. Any change that alters a
        /// signature has changed locked-cadence behaviour, which is not permitted.
        /// </summary>
        [Test]
        public void Locked_ReportSignatureGrid()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("StepRate,FrameRate,Ticks,Snaps,Signature");

            foreach (float stepRate in StepRates)
            foreach (float frameRate in FrameRates)
            {
                Run run = Drive(stepRate, frameRate, cadenceJitter: 0f);
                sb.AppendLine(
                    $"{stepRate},{frameRate},{run.Output.Count},{run.SnapTimes.Count}," +
                    $"{Signature(run.Output):X8}");
            }

            Debug.Log("LOCKED CADENCE BASELINE\n" + sb);
        }

        // FNV-1a over the raw float bits of every emitted component.
        private static uint Signature(List<Quaternion> poses)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (Quaternion q in poses)
                {
                    var k = Key(q);
                    foreach (int bits in new[] { k.Item1, k.Item2, k.Item3, k.Item4 })
                    {
                        h ^= (uint)bits;
                        h *= 16777619u;
                    }
                }
                return h;
            }
        }

        private static (int, int, int, int) Key(Quaternion q) => (
            System.BitConverter.SingleToInt32Bits(q.x),
            System.BitConverter.SingleToInt32Bits(q.y),
            System.BitConverter.SingleToInt32Bits(q.z),
            System.BitConverter.SingleToInt32Bits(q.w));
    }
}
