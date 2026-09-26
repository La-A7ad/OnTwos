using System.Collections.Generic;
using NUnit.Framework;
using OnTwos.Runtime;
using OnTwos.Runtime.Utilities;
using UnityEngine;

namespace OnTwos.Tests
{
    /// <summary>
    /// Bone rules decide which bones step, how hard, and under what response curve.
    /// Every failure mode here is silent: the wrong bone is excluded, or an edit in the
    /// inspector appears to do nothing. Nothing throws, so nothing surfaces except as
    /// "the feature doesn't work".
    ///
    /// The two Sync_DetectsAnInPlaceEdit* tests are regression guards for a defect fixed
    /// on 2026-09-26: change detection compared array identity and bone NAME strings but
    /// never the rule VALUES, so editing a tau or ticking an exclude box in Play mode did
    /// not re-resolve — contradicting DOCUMENTATION.md section 9, which promises it does.
    /// </summary>
    public class BoneRuleSetTests
    {
        private readonly List<GameObject> _spawned = new List<GameObject>();

        private Transform Bone(string name)
        {
            var go = new GameObject(name);
            _spawned.Add(go);
            return go.transform;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _spawned)
                if (go != null) Object.DestroyImmediate(go);
            _spawned.Clear();
        }

        private static OnTwosProfile.BoneOverride Override(
            string nameContains, bool forceExclude = false, float tau = 0f) =>
            new OnTwosProfile.BoneOverride
            {
                NameContains = nameContains,
                ForceExclude = forceExclude,
                TauOverride  = tau
            };

        // ------------------------------------------------------------------ resolution

        [Test]
        public void Sync_ResolvesArraysIndexParallelToTheBones()
        {
            var rules = new BoneRuleSet();
            var bones = new[] { Bone("Hips"), Bone("Spine"), Bone("Head") };

            Assert.IsTrue(rules.Sync(bones, null, null, null, null), "first Sync must resolve");

            Assert.AreEqual(3, rules.Count);
            Assert.AreEqual(3, rules.Excluded.Length);
            Assert.AreEqual(3, rules.TauOverride.Length);
            Assert.AreEqual(3, rules.ResponseCurve.Length);
        }

        [Test]
        public void Sync_WithNothingChanged_IsANoOp()
        {
            var rules = new BoneRuleSet();
            var bones = new[] { Bone("Hips"), Bone("Spine") };

            rules.Sync(bones, null, null, null, null);
            Assert.IsFalse(rules.Sync(bones, null, null, null, null),
                "re-resolving every frame is what this guard exists to prevent");
        }

        [Test]
        public void ExcludeKeywords_MatchSubstringsCaseInsensitively()
        {
            var rules = new BoneRuleSet();
            var bones = new[] { Bone("mixamorig:LeftFoot"), Bone("mixamorig:Spine") };

            rules.Sync(bones, null, new[] { "FOOT" }, null, null);

            Assert.IsTrue(rules.Excluded[0], "keyword matching must ignore case");
            Assert.IsFalse(rules.Excluded[1]);
        }

        [Test]
        public void ExcludeBones_MatchByDirectReference()
        {
            var rules = new BoneRuleSet();
            Transform a = Bone("Alpha"), b = Bone("Beta");

            rules.Sync(new[] { a, b }, new[] { b }, null, null, null);

            Assert.IsFalse(rules.Excluded[0]);
            Assert.IsTrue(rules.Excluded[1]);
        }

        [Test]
        public void BoneOverrides_ForceExcludeAndTauApply()
        {
            var rules = new BoneRuleSet();
            var bones = new[] { Bone("Head"), Bone("PropHand") };
            var overrides = new[] { Override("head", tau: 3f), Override("prop", forceExclude: true) };

            rules.Sync(bones, null, null, overrides, null);

            Assert.AreEqual(3f, rules.TauOverride[0], 1e-5f);
            Assert.IsTrue(rules.Excluded[1]);
        }

        [Test]
        public void BoneTunings_TakePrecedenceOverBoneOverrides()
        {
            var rules = new BoneRuleSet();
            Transform head = Bone("Head");
            var overrides = new[] { Override("head", tau: 3f) };
            var tunings   = new[] { new BoneTuning { Bone = head, TauOverride = 17f } };

            rules.Sync(new[] { head }, null, null, overrides, tunings);

            Assert.AreEqual(17f, rules.TauOverride[0], 1e-5f,
                "BoneTunings is documented as the highest-precedence tier");
        }

        [Test]
        public void BoneTunings_ExcludeBeatsAKeywordThatWouldNotHaveMatched()
        {
            var rules = new BoneRuleSet();
            Transform foot = Bone("ankle_L");
            var tunings = new[] { new BoneTuning { Bone = foot, Exclude = true } };

            rules.Sync(new[] { foot }, null, new[] { "foot" }, null, tunings);

            Assert.IsTrue(rules.Excluded[0],
                "a direct reference must work regardless of the rig's naming convention");
        }

        [Test]
        public void BoneTuning_WithAnEmptyCurve_DoesNotOverrideTheGlobalCurve()
        {
            // An AnimationCurve with no keys evaluates to 0 for every input, which would
            // drive tau to zero and make the bone snap on every candidate.
            var rules = new BoneRuleSet();
            Transform head = Bone("Head");
            var tunings = new[]
            {
                new BoneTuning { Bone = head, ResponseCurveOverride = new AnimationCurve() }
            };

            rules.Sync(new[] { head }, null, null, null, tunings);

            Assert.IsNull(rules.ResponseCurve[0],
                "an unauthored curve must read as 'not set', not as a curve that returns zero");
        }

        [Test]
        public void BoneTuning_WithANullBoneReference_IsIgnored()
        {
            var rules = new BoneRuleSet();
            Transform head = Bone("Head");
            var tunings = new[] { new BoneTuning { Bone = null, Exclude = true } };

            Assert.DoesNotThrow(() => rules.Sync(new[] { head }, null, null, null, tunings));
            Assert.IsFalse(rules.Excluded[0], "an empty tuning slot must not exclude an arbitrary bone");
        }

        [Test]
        public void Sync_WithNullBonesInTheArray_DoesNotThrow()
        {
            var rules = new BoneRuleSet();
            var bones = new[] { Bone("Hips"), null, Bone("Head") };

            Assert.DoesNotThrow(() => rules.Sync(bones, null, new[] { "head" }, null, null));
            Assert.AreEqual(3, rules.Count);
        }

        // ------------------------------------------------------------------ change detection

        [Test]
        public void Sync_DetectsABoneCountChange()
        {
            var rules = new BoneRuleSet();
            var three = new[] { Bone("A"), Bone("B"), Bone("C") };
            rules.Sync(three, null, null, null, null);

            var two = new[] { three[0], three[2] };
            Assert.IsTrue(rules.Sync(two, null, null, null, null),
                "a prune changes the bone array and must force a re-resolve");
            Assert.AreEqual(2, rules.Count);
        }

        [Test]
        public void Sync_DetectsAReplacedTuningArray()
        {
            var rules = new BoneRuleSet();
            Transform head = Bone("Head");
            rules.Sync(new[] { head }, null, null, null,
                       new[] { new BoneTuning { Bone = head, TauOverride = 5f } });

            Assert.IsTrue(rules.Sync(new[] { head }, null, null, null,
                          new[] { new BoneTuning { Bone = head, TauOverride = 9f } }),
                "adding or removing a list element replaces the array and is detected");
            Assert.AreEqual(9f, rules.TauOverride[0], 1e-5f);
        }

        [Test]
        public void Sync_DetectsAnEditedKeywordString()
        {
            var rules = new BoneRuleSet();
            var bones = new[] { Bone("LeftFoot") };
            var keywords = new[] { "hand" };
            rules.Sync(bones, null, keywords, null, null);
            Assert.IsFalse(rules.Excluded[0]);

            keywords[0] = "foot";   // in-place edit of the string element

            Assert.IsTrue(rules.Sync(bones, null, keywords, null, null));
            Assert.IsTrue(rules.Excluded[0]);
        }

        [Test]
        public void Sync_DetectsAnInPlaceEditToABoneTuningValue()
        {
            // Editing TauOverride in the inspector mutates the existing element and leaves
            // the array reference alone. Sync used to compare _tunings by reference only,
            // so Resolve() never ran and the edit appeared to do nothing.
            var rules = new BoneRuleSet();
            Transform head = Bone("Head");
            var tunings = new[] { new BoneTuning { Bone = head, TauOverride = 5f } };

            rules.Sync(new[] { head }, null, null, null, tunings);
            Assert.AreEqual(5f, rules.TauOverride[0], 1e-5f);

            tunings[0].TauOverride = 20f;   // what the inspector does

            rules.Sync(new[] { head }, null, null, null, tunings);
            Assert.AreEqual(20f, rules.TauOverride[0], 1e-5f,
                "an in-place tuning edit did not re-resolve");
        }

        [Test]
        public void Sync_DetectsAnInPlaceEditToABoneOverrideValue()
        {
            // OverridesChanged used to compare only NameContains, so toggling ForceExclude
            // or changing TauOverride left the name identical and nothing re-resolved.
            var rules = new BoneRuleSet();
            Transform head = Bone("Head");
            var overrides = new[] { Override("head", forceExclude: false, tau: 4f) };

            rules.Sync(new[] { head }, null, null, overrides, null);
            Assert.IsFalse(rules.Excluded[0]);

            overrides[0].ForceExclude = true;

            rules.Sync(new[] { head }, null, null, overrides, null);
            Assert.IsTrue(rules.Excluded[0],
                "toggling ForceExclude did not re-resolve");
        }
    }
}
