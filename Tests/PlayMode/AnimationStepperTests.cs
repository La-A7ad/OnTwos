using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using OnTwos.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace OnTwos.Tests.PlayMode
{
    /// <summary>
    /// AnimationStepper is an authoring tool rather than a shipping system, so its
    /// runtime cost does not matter — but its correctness does, because the bake window
    /// runs the same pipeline and a baked clip inherits whatever it gets wrong.
    ///
    /// Driven in AnySource mode throughout. AnimatorDriven needs an imported rig and an
    /// AnimatorController asset, which would make these tests depend on project content;
    /// AnySource exercises the same read-step-write path with the source replaced by a
    /// script, which is exactly what the mode exists for.
    /// </summary>
    public class AnimationStepperTests
    {
        private RigFactory.Scope _scope;

        [SetUp]
        public void SetUp() => _scope = new RigFactory.Scope();

        [TearDown]
        public void TearDown() => _scope.Dispose();

        /// <summary>
        /// Writes a continuous rotation onto bones every Update, which is the frame
        /// phase an Animator, an IK solver or a motion-matching system would write in.
        /// LateUpdate — where the stepper reads — always follows it.
        /// </summary>
        private sealed class RotationDriver : MonoBehaviour
        {
            public Transform[] Bones;
            public float DegreesPerSecond = 120f;
            public readonly List<Quaternion> LastWritten = new List<Quaternion>();

            private void Update()
            {
                LastWritten.Clear();
                float angle = Time.time * DegreesPerSecond;
                for (int i = 0; i < Bones.Length; i++)
                {
                    var q = Quaternion.AngleAxis(angle + i * 11f, Vector3.forward);
                    Bones[i].localRotation = q;
                    LastWritten.Add(q);
                }
            }
        }

        private static Transform[] ChainBones(Transform root)
        {
            var list = new List<Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>())
                if (t.name.StartsWith("Bone")) list.Add(t);
            return list.ToArray();
        }

        private AnimationStepper Spawn(Transform root, out RotationDriver driver, float stepRate = 6f)
        {
            Transform[] bones = ChainBones(root);

            driver = root.gameObject.AddComponent<RotationDriver>();
            driver.Bones = bones;

            var s = root.gameObject.AddComponent<AnimationStepper>();
            s.Mode     = AnimationStepper.StepperMode.AnySource;
            s.BoneRoot = root;
            s.Profile  = null;              // exercise the inspector-field fallback
            s.Tau      = 3f;
            s.StepRate = stepRate;
            s.CadenceJitter = 0f;
            return s;
        }

        [UnityTest]
        public IEnumerator AnySource_DiscoversBonesWithoutAnAnimator()
        {
            Transform root = RigFactory.CreateBoneChain(_scope, 5);
            AnimationStepper s = Spawn(root, out _);

            yield return null;

            Assert.IsNotNull(s.Bones);
            Assert.GreaterOrEqual(s.Bones.Length, 5, "every bone under BoneRoot should be tracked");
            Assert.AreEqual(s.Bones.Length, s.BoneExcluded.Length,
                "the exclusion flags are index-parallel to the bone list by contract");
        }

        [UnityTest]
        public IEnumerator AnySource_HoldsThePoseBetweenSteps()
        {
            // The whole point of the system: with a 6 Hz cadence at 60 fps, the written
            // pose must repeat for several frames at a time rather than change every frame.
            Transform root = RigFactory.CreateBoneChain(_scope, 3);
            AnimationStepper s = Spawn(root, out _, stepRate: 6f);

            yield return null;
            yield return null;

            Transform bone = s.Bones[0];
            var observed = new List<Quaternion>();
            for (int i = 0; i < 60; i++)
            {
                yield return null;
                observed.Add(bone.localRotation);
            }

            int changes = 0;
            for (int i = 1; i < observed.Count; i++)
                if (Quaternion.Angle(observed[i - 1], observed[i]) > 1e-3f) changes++;

            Assert.Less(changes, observed.Count / 2,
                $"the pose changed on {changes} of {observed.Count} frames — that is not stepped");
            Assert.Greater(changes, 0, "the pose never changed at all; the stepper is not running");
        }

        [UnityTest]
        public IEnumerator AnySource_ExcludedBoneTracksItsSourceEveryFrame()
        {
            Transform root = RigFactory.CreateBoneChain(_scope, 4);
            AnimationStepper s = Spawn(root, out RotationDriver driver, stepRate: 2f);
            s.BoneTunings = new[] { new BoneTuning { Bone = driver.Bones[1], Exclude = true } };

            yield return null;
            yield return null;

            for (int i = 0; i < 30; i++)
            {
                yield return null;
                float angle = Quaternion.Angle(driver.LastWritten[1], driver.Bones[1].localRotation);
                Assert.Less(angle, 1e-2f,
                    "an excluded bone must keep the unstepped source rotation it was given");
            }
        }

        [UnityTest]
        public IEnumerator ExcludeKeywords_ExcludeMatchingBones()
        {
            Transform root = RigFactory.CreateBoneChain(_scope, 4);
            AnimationStepper s = Spawn(root, out _);
            s.ExcludeKeywords = new[] { "bone2" };

            yield return null;
            yield return null;

            bool sawExclusion = false;
            for (int i = 0; i < s.Bones.Length; i++)
                if (s.Bones[i].name.ToLowerInvariant().Contains("bone2"))
                {
                    Assert.IsTrue(s.BoneExcluded[i], $"{s.Bones[i].name} matched the keyword but was not excluded");
                    sawExclusion = true;
                }

            Assert.IsTrue(sawExclusion, "test setup produced no bone matching the keyword");
        }

        [UnityTest]
        public IEnumerator FlushAllHolds_DoesNotThrowAndResumesCleanly()
        {
            Transform root = RigFactory.CreateBoneChain(_scope, 4);
            AnimationStepper s = Spawn(root, out _);

            yield return null;
            yield return null;

            Assert.DoesNotThrow(() => s.FlushAllHolds());

            for (int i = 0; i < 10; i++) yield return null;

            foreach (Transform b in s.Bones)
                Assert.IsFalse(float.IsNaN(b.localRotation.x),
                    "a flush must not leave a bone holding a NaN pose");
        }

        [UnityTest]
        public IEnumerator FlushAllHolds_BeforeStart_IsSafe()
        {
            // Callers reach for this on a teleport or a mode switch, which can land on
            // the same frame the component is added.
            Transform root = RigFactory.CreateBoneChain(_scope, 3);
            var s = root.gameObject.AddComponent<AnimationStepper>();
            s.Mode     = AnimationStepper.StepperMode.AnySource;
            s.BoneRoot = root;

            Assert.DoesNotThrow(() => s.FlushAllHolds());
            yield return null;
        }

        [UnityTest]
        public IEnumerator Deactivate_ReturnsBonesToTheirSourceMotion()
        {
            Transform root = RigFactory.CreateBoneChain(_scope, 3);
            AnimationStepper s = Spawn(root, out RotationDriver driver, stepRate: 2f);

            yield return null;
            yield return null;

            s.Deactivate();

            for (int i = 0; i < 10; i++) yield return null;

            float angle = Quaternion.Angle(driver.LastWritten[0], driver.Bones[0].localRotation);
            Assert.Less(angle, 1e-2f,
                "after Deactivate the source motion must pass through untouched");
        }

        [UnityTest]
        public IEnumerator LiveTauChange_IsPickedUpWithoutARestart()
        {
            Transform root = RigFactory.CreateBoneChain(_scope, 3);
            AnimationStepper s = Spawn(root, out _, stepRate: 4f);

            yield return null;
            yield return null;

            Assert.DoesNotThrow(() =>
            {
                s.Tau = 25f;
                s.StepRate = 12f;
                s.CadenceJitter = 0.5f;
            });

            for (int i = 0; i < 20; i++) yield return null;

            foreach (Transform b in s.Bones)
                Assert.IsFalse(float.IsNaN(b.localRotation.x));
        }

        [UnityTest]
        public IEnumerator DivergenceSignal_ReportsNonZeroForMovingBones()
        {
            // The smear signal layer: raw minus held, measured at the bone tip. A pure
            // rotation produces zero displacement AT the joint, so a zero here would
            // mean the lever arm was lost.
            Transform root = RigFactory.CreateBoneChain(_scope, 4);
            AnimationStepper s = Spawn(root, out _, stepRate: 2f);
            s.EnableBoneDivergence = true;

            yield return null;
            s.EnableDivergenceSignal();
            yield return null;

            bool sawMovement = false;
            for (int i = 0; i < 40; i++)
            {
                yield return null;
                if (s.BoneDivergence == null) continue;
                foreach (Vector3 d in s.BoneDivergence)
                    if (d.sqrMagnitude > 1e-8f) { sawMovement = true; break; }
                if (sawMovement) break;
            }

            Assert.IsTrue(sawMovement,
                "a bone held off a moving source must report a non-zero tip displacement");
        }

        [UnityTest]
        public IEnumerator EmptyBoneRoot_DoesNotThrow()
        {
            var lonely = new GameObject("NoBones");
            _scope.Track(lonely);

            var s = lonely.AddComponent<AnimationStepper>();
            s.Mode     = AnimationStepper.StepperMode.AnySource;
            s.BoneRoot = lonely.transform;

            yield return null;
            yield return null;

            Assert.Pass("a rig with no child bones must not throw from the per-frame loop");
        }
    }
}
