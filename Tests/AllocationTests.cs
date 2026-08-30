using System;
using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Steady-state allocation behaviour of the hot path.
    ///
    /// TECHNICAL.md §7 claims 0 B/frame after warm-up. These tests measure it with
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>, which counts managed allocation
    /// exactly rather than sampling like the Profiler does.
    ///
    /// The measured region does no recording of its own, because a List.Add in the loop
    /// would be indistinguishable from an allocation inside the scheduler.
    /// </summary>
    public class AllocationTests
    {
        private const float Tau = 5f;
        private const int CandidatesPerSegment = 2;
        private const int BufferSize = 30;

        private const int WarmupTicks = 240;   // fills the ring buffer and every pooled list
        private const int MeasuredTicks = 3600; // 60 s at 60 Hz

        private static HoldFrameScheduler MakeScheduler(float stepRate, float cadenceJitter)
        {
            var s = new HoldFrameScheduler(Tau, CandidatesPerSegment, BufferSize);
            s.MaxHoldSeconds = 1f / stepRate;
            s.MinHoldSeconds = s.MaxHoldSeconds * (1f - cadenceJitter);
            return s;
        }

        private static long MeasureBytes(float stepRate, float cadenceJitter, float frameRate)
        {
            HoldFrameScheduler s = MakeScheduler(stepRate, cadenceJitter);
            float dt = 1f / frameRate;

            for (int i = 0; i < WarmupTicks; i++)
                s.Update(i * dt, SyntheticMotion.Swing(i * dt));

            // Settle any allocation the warm-up left pending before taking the reading.
            GC.Collect();
            GC.WaitForPendingFinalizers();

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = WarmupTicks; i < WarmupTicks + MeasuredTicks; i++)
            {
                float t = i * dt;
                s.Update(t, SyntheticMotion.Swing(t));
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [Test]
        public void LockedCadence_AllocatesNothingInSteadyState()
        {
            long bytes = MeasureBytes(stepRate: 12f, cadenceJitter: 0f, frameRate: 60f);
            Debug.Log($"Locked steady-state allocation: {bytes} B over {MeasuredTicks} ticks " +
                      $"({bytes / (float)MeasuredTicks:F3} B/tick)");
            Assert.AreEqual(0, bytes, $"locked hot path allocated {bytes} B");
        }

        [Test]
        public void AdaptiveCadence_AllocatesNothingInSteadyState()
        {
            long bytes = MeasureBytes(stepRate: 12f, cadenceJitter: 0.5f, frameRate: 60f);
            Debug.Log($"Adaptive steady-state allocation: {bytes} B over {MeasuredTicks} ticks " +
                      $"({bytes / (float)MeasuredTicks:F3} B/tick)");
            Assert.AreEqual(0, bytes, $"adaptive hot path allocated {bytes} B");
        }

        /// <summary>
        /// Cost comparison between the two regimes. Reported, not asserted — wall-clock
        /// timing in EditMode is too noisy to gate a build on, but the ratio is the
        /// figure TECHNICAL.md §7 quotes (40.4 us -> 1.5 us per 13-bone rig per tick).
        /// </summary>
        [Test]
        public void ReportPerTickCost()
        {
            const int bones = 13;
            const int ticks = 20000;

            foreach (float jitter in new[] { 0f, 0.5f })
            {
                var rig = new HoldFrameScheduler[bones];
                for (int b = 0; b < bones; b++) rig[b] = MakeScheduler(12f, jitter);

                float dt = 1f / 50f; // RagdollStepper's fixed tick
                for (int i = 0; i < 400; i++)
                    for (int b = 0; b < bones; b++)
                        rig[b].Update(i * dt, SyntheticMotion.Swing(i * dt + b));

                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 400; i < 400 + ticks; i++)
                {
                    float t = i * dt;
                    for (int b = 0; b < bones; b++)
                        rig[b].Update(t, SyntheticMotion.Swing(t + b));
                }
                sw.Stop();

                double usPerRigTick = sw.Elapsed.TotalMilliseconds * 1000.0 / ticks;
                Debug.Log($"CadenceJitter={jitter}: {usPerRigTick:F2} us per {bones}-bone rig per tick");
            }
        }
    }
}
