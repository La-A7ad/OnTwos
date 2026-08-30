using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Measures adaptive-mode output so that changes to candidate placement can be
    /// quantified rather than asserted about.
    ///
    /// Reports three things per configuration:
    ///   - a signature of the emitted pose sequence, so an inert change is immediately
    ///     visible as an unchanged signature;
    ///   - snap count, since a change in placement may change how often the Tau walk
    ///     fires at all;
    ///   - reconstruction error against the source motion in degrees, mean and peak.
    ///
    /// Peak error is the interesting one. The claim behind arc-length placement is that
    /// snaps are spent where the motion is, so at a matched pose budget it should hold
    /// the source curve more tightly at its extremes than uniform placement would.
    /// </summary>
    public class AdaptiveDeltaTests
    {
        private static readonly (float step, float fps, float jitter)[] Configs =
        {
            (12f, 60f, 0.25f),
            (12f, 60f, 0.50f),
            (12f, 60f, 1.00f),
            (8f,  60f, 0.50f),
            (24f, 60f, 0.50f),
            (12f, 30f, 0.50f),
            (12f, 144f, 0.50f),
        };

        [Test]
        public void Adaptive_ReportOutputAndError()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("StepRate,fps,Jitter,Snaps,MeanErrDeg,MaxErrDeg,Signature");

            foreach (var (step, fps, jitter) in Configs)
            {
                LockedParityTests.Run run = LockedParityTests.Drive(step, fps, jitter);

                double sum = 0;
                float max = 0f;
                for (int i = 0; i < run.Output.Count; i++)
                {
                    float e = Quaternion.Angle(run.Output[i], run.RawInput[i]);
                    sum += e;
                    if (e > max) max = e;
                }

                sb.AppendLine(
                    $"{step},{fps},{jitter},{run.SnapTimes.Count}," +
                    $"{sum / run.Output.Count:F4},{max:F4},{Signature(run.Output):X8}");
            }

            Debug.Log("ADAPTIVE OUTPUT BASELINE\n" + sb);
        }

        private static uint Signature(List<Quaternion> poses)
        {
            unchecked
            {
                uint h = 2166136261u;
                foreach (Quaternion q in poses)
                {
                    foreach (float f in new[] { q.x, q.y, q.z, q.w })
                    {
                        h ^= (uint)System.BitConverter.SingleToInt32Bits(f);
                        h *= 16777619u;
                    }
                }
                return h;
            }
        }
    }
}
