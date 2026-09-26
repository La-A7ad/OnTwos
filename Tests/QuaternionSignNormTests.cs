using NUnit.Framework;
using OnTwos.Runtime.Math;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// q and -q are the same rotation on opposite hemispheres of the 4-sphere. Fitting
    /// across a flip makes the interpolant take a ~360 degree detour that PCHIP
    /// reproduces faithfully, which reads as a violent spin spike. These pin the fix.
    /// </summary>
    public class QuaternionSignNormTests
    {
        private static Quaternion Neg(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, -q.w);

        [Test]
        public void Normalise_LeavesTheFirstSampleUntouched()
        {
            var q = new[] { SyntheticMotion.AboutAxis(Vector3.up, 30f),
                            SyntheticMotion.AboutAxis(Vector3.up, 40f) };
            Quaternion first = q[0];

            QuaternionSignNorm.Normalise(q, q.Length);

            Assert.IsTrue(SyntheticMotion.BitwiseEquals(first, q[0]),
                "the first sample is the canonical reference and must not be flipped");
        }

        [Test]
        public void Normalise_FlipsSamplesOnTheOppositeHemisphere()
        {
            Quaternion a = SyntheticMotion.AboutAxis(Vector3.up, 30f);
            Quaternion b = SyntheticMotion.AboutAxis(Vector3.up, 35f);
            var q = new[] { a, Neg(b) };

            QuaternionSignNorm.Normalise(q, q.Length);

            Assert.Greater(Quaternion.Dot(q[0], q[1]), 0f,
                "consecutive samples must end up on the same hemisphere");
            Assert.IsTrue(SyntheticMotion.BitwiseEquals(b, q[1]));
        }

        [Test]
        public void Normalise_PreservesTheRotationItRepresents()
        {
            var q = new Quaternion[12];
            for (int i = 0; i < q.Length; i++)
            {
                Quaternion r = SyntheticMotion.AboutAxis(Vector3.forward, 10f * i);
                q[i] = (i % 3 == 0) ? Neg(r) : r;
            }
            var original = (Quaternion[])q.Clone();

            QuaternionSignNorm.Normalise(q, q.Length);

            for (int i = 0; i < q.Length; i++)
                Assert.LessOrEqual(Quaternion.Angle(original[i], q[i]), 1e-3f,
                    $"sample {i} changed rotation, not just sign");
        }

        [Test]
        public void Normalise_MakesEveryAdjacentPairNonNegative()
        {
            var q = new Quaternion[30];
            for (int i = 0; i < q.Length; i++)
            {
                Quaternion r = SyntheticMotion.AboutAxis(new Vector3(1f, 1f, 0f), 17f * i);
                q[i] = (i % 2 == 0) ? Neg(r) : r;
            }

            QuaternionSignNorm.Normalise(q, q.Length);

            for (int i = 1; i < q.Length; i++)
                Assert.GreaterOrEqual(Quaternion.Dot(q[i - 1], q[i]), 0f, $"pair {i - 1}->{i}");
        }

        [Test]
        public void Normalise_IsIdempotent()
        {
            var q = new Quaternion[16];
            for (int i = 0; i < q.Length; i++)
            {
                Quaternion r = SyntheticMotion.AboutAxis(Vector3.right, 23f * i);
                q[i] = (i % 4 == 0) ? Neg(r) : r;
            }

            QuaternionSignNorm.Normalise(q, q.Length);
            var once = (Quaternion[])q.Clone();
            QuaternionSignNorm.Normalise(q, q.Length);

            for (int i = 0; i < q.Length; i++)
                Assert.IsTrue(SyntheticMotion.BitwiseEquals(once[i], q[i]),
                    $"second pass changed sample {i}");
        }

        [Test]
        public void Normalise_RespectsTheCountArgument()
        {
            Quaternion a = SyntheticMotion.AboutAxis(Vector3.up, 10f);
            Quaternion tail = Neg(SyntheticMotion.AboutAxis(Vector3.up, 20f));
            var q = new[] { a, Neg(SyntheticMotion.AboutAxis(Vector3.up, 15f)), tail };

            QuaternionSignNorm.Normalise(q, 2);

            Assert.IsTrue(SyntheticMotion.BitwiseEquals(tail, q[2]),
                "elements past count must be left alone — the ring buffer reuses the tail");
        }

        [Test]
        public void Normalise_ShortInputs_DoNotThrow()
        {
            Assert.DoesNotThrow(() => QuaternionSignNorm.Normalise(new Quaternion[0], 0));
            Assert.DoesNotThrow(() => QuaternionSignNorm.Normalise(new[] { Quaternion.identity }, 1));
        }
    }
}
