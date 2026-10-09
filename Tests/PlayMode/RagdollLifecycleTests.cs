using System;
using System.Collections;
using NUnit.Framework;
using OnTwos.Runtime;
using UnityEngine;
using UnityEngine.TestTools;

namespace OnTwos.Tests.PlayMode
{
    /// <summary>
    /// RagdollStepper is the only OnTwos system that runs in a shipped scene, so it is
    /// the only one whose runtime behaviour has to be right rather than merely
    /// convenient. Nothing in this file existed before: the whole settle-sleep-wake
    /// lifecycle, the prune path and the proxy handover have only ever been verified by
    /// looking at them in the editor.
    /// </summary>
    public class RagdollLifecycleTests
    {
        private RigFactory.Scope _scope;
        private float _originalFixedDelta;

        [SetUp]
        public void SetUp()
        {
            _scope = new RigFactory.Scope();
            _originalFixedDelta = Time.fixedDeltaTime;
            Time.fixedDeltaTime = 0.02f;   // pin the physics clock so timings are reproducible
        }

        [TearDown]
        public void TearDown()
        {
            _scope.Dispose();
            Time.fixedDeltaTime = _originalFixedDelta;
        }

        private static IEnumerator FixedSteps(int n)
        {
            for (int i = 0; i < n; i++) yield return new WaitForFixedUpdate();
        }

        /// <summary>Pump fixed steps until the predicate holds or the budget runs out.</summary>
        private static IEnumerator WaitForCondition(Func<bool> predicate, float timeoutSeconds, string what)
        {
            int budget = Mathf.CeilToInt(timeoutSeconds / Time.fixedDeltaTime);
            for (int i = 0; i < budget; i++)
            {
                if (predicate()) yield break;
                yield return new WaitForFixedUpdate();
            }
            Assert.Fail($"timed out after {timeoutSeconds}s waiting for: {what}");
        }

        private RagdollStepper Spawn(GameObject rig, OnTwosProfile profile)
        {
            var stepper = rig.AddComponent<RagdollStepper>();
            stepper.Profile = profile;
            return stepper;
        }

        // ------------------------------------------------------------------ proxy handover

        [UnityTest]
        public IEnumerator Start_BuildsAProxyAndHidesTheSourceRenderers()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 3f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile());

            yield return null;

            Assert.IsNotNull(s.VisualProxy, "the proxy is built in Start");
            _scope.Track(s.VisualProxy);

            foreach (Renderer r in rig.GetComponentsInChildren<Renderer>(true))
                Assert.IsFalse(r.enabled,
                    "the source must be invisible — only the stepped proxy is seen");
        }

        [UnityTest]
        public IEnumerator SingleBodyRig_IsTrackedRatherThanFailingSilently()
        {
            // Documented trap: a rig with one Rigidbody and no colliders logs "1 tracked
            // bones" as ordinary info and then free-falls forever. This pins that the
            // single-body case at least builds and steps.
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateSingleBody(_scope, new Vector3(0f, 2f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile());

            yield return null;
            Assert.IsNotNull(s.VisualProxy);
            _scope.Track(s.VisualProxy);

            yield return FixedSteps(30);
            Assert.IsTrue(s.VisualProxy != null, "a one-body rig must survive 30 physics ticks");
        }

        [UnityTest]
        public IEnumerator ProxyFollowsTheSourceWhileFalling()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 5f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(stepRate: 24f));

            yield return null;
            _scope.Track(s.VisualProxy);

            yield return FixedSteps(25);

            Rigidbody sourceRoot = rig.GetComponentInChildren<Rigidbody>();
            Transform proxyRoot  = s.VisualProxy.transform.GetChild(0);

            Assert.Less(Vector3.Distance(sourceRoot.position, proxyRoot.position), 1.5f,
                "the proxy must track the falling source, not stay at the spawn point");
        }

        // ------------------------------------------------------------------ settle

        [UnityTest]
        public IEnumerator Settles_AndFiresOnSettledExactlyOnce()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateSingleBody(_scope, new Vector3(0f, 0.6f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f));

            int settledCount = 0;
            yield return null;
            _scope.Track(s.VisualProxy);
            s.OnSettled += () => settledCount++;

            yield return WaitForCondition(() => s.IsSettled, 10f, "the body to settle");

            yield return FixedSteps(60);   // stay settled

            Assert.AreEqual(1, settledCount, "OnSettled must fire once, not once per tick");
            Assert.IsTrue(s.IsSettled);
        }

        [UnityTest]
        public IEnumerator Settling_PutsEveryTrackedBodyToSleep()
        {
            // This is what makes a resting corpse free: FixedUpdate returns immediately
            // AND PhysX drops the joint island out of the solver.
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateSingleBody(_scope, new Vector3(0f, 0.6f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f));

            yield return null;
            _scope.Track(s.VisualProxy);

            yield return WaitForCondition(() => s.IsSettled, 10f, "the body to settle");
            yield return FixedSteps(2);

            foreach (Rigidbody rb in rig.GetComponentsInChildren<Rigidbody>())
                Assert.IsTrue(rb.IsSleeping(),
                    "a settled body left awake keeps the whole island in the solver forever");
        }

        [UnityTest]
        public IEnumerator SettledProxy_StopsMoving()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateSingleBody(_scope, new Vector3(0f, 0.6f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f));

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return WaitForCondition(() => s.IsSettled, 10f, "the body to settle");

            Transform proxyBone = s.VisualProxy.transform;
            Vector3 at = proxyBone.position;
            Quaternion rot = proxyBone.rotation;

            yield return FixedSteps(50);

            Assert.Less(Vector3.Distance(at, proxyBone.position), 1e-4f);
            Assert.Less(Quaternion.Angle(rot, proxyBone.rotation), 1e-2f);
        }

        // ------------------------------------------------------------------ wake

        [UnityTest]
        public IEnumerator AnImpulseWakesASettledRagdollAndFiresOnWokeOnce()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 1.2f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f));

            int wokeCount = 0;
            yield return null;
            _scope.Track(s.VisualProxy);
            s.OnWoke += () => wokeCount++;

            yield return WaitForCondition(() => s.IsSettled, 15f, "the chain to settle");

            rig.GetComponentInChildren<Rigidbody>().AddForce(new Vector3(6f, 3f, 0f), ForceMode.Impulse);

            yield return WaitForCondition(() => !s.IsSettled, 2f, "the impulse to wake it");
            yield return FixedSteps(10);

            Assert.AreEqual(1, wokeCount, "OnWoke must fire once per wake, not per tick");
        }

        [UnityTest]
        public IEnumerator WakingAnOutstretchedLimbWakesTheWholeRig()
        {
            // The defect this lifecycle replaced: settling considered every body while
            // waking considered one, so an impact on a limb that did not move the root
            // left the proxy frozen while the ragdoll moved underneath it.
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 4, new Vector3(0f, 1.5f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f));

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return WaitForCondition(() => s.IsSettled, 15f, "the chain to settle");

            Rigidbody[] bodies = rig.GetComponentsInChildren<Rigidbody>();
            bodies[bodies.Length - 1].AddForce(new Vector3(0f, 5f, 3f), ForceMode.Impulse);

            yield return WaitForCondition(() => !s.IsSettled, 2f,
                "an impulse on the last link to wake the rig");
        }

        [UnityTest]
        public IEnumerator WokenProxyResumesTracking()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateSingleBody(_scope, new Vector3(0f, 0.6f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f, stepRate: 24f));

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return WaitForCondition(() => s.IsSettled, 10f, "the body to settle");

            Vector3 restingProxyPos = s.VisualProxy.transform.position;

            rig.GetComponent<Rigidbody>().AddForce(new Vector3(0f, 9f, 0f), ForceMode.Impulse);
            yield return WaitForCondition(() => !s.IsSettled, 2f, "the impulse to wake it");
            yield return FixedSteps(20);

            Assert.Greater(Vector3.Distance(restingProxyPos, s.VisualProxy.transform.position), 0.05f,
                "after waking, the proxy must step the impact rather than stay frozen");
        }

        [UnityTest]
        public IEnumerator DirectTransformWriteDoesNotWake_AsDocumented()
        {
            // Documented caveat, pinned so it stays a known limitation rather than
            // becoming a surprise: writing Transform bypasses PhysX, so the sleep flag
            // never clears and the proxy stays put.
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateSingleBody(_scope, new Vector3(0f, 0.6f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(settleTime: 0.1f));

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return WaitForCondition(() => s.IsSettled, 10f, "the body to settle");

            rig.transform.position += new Vector3(3f, 0f, 0f);
            yield return FixedSteps(20);

            Assert.IsTrue(s.IsSettled,
                "DOCUMENTATION.md promises this does NOT wake the rig — call WakeUp() instead");
        }

        // ------------------------------------------------------------------ dismemberment

        [UnityTest]
        public IEnumerator DestroyingALimb_PrunesItAndKeepsSteppingTheRest()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 4, new Vector3(0f, 4f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile());

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return FixedSteps(10);

            Rigidbody[] bodies = rig.GetComponentsInChildren<Rigidbody>();
            UnityEngine.Object.Destroy(bodies[bodies.Length - 1].gameObject);

            yield return FixedSteps(10);

            Assert.DoesNotThrow(() => { var _ = s.IsSettled; },
                "the stepper must survive a destroyed body");
            Assert.IsTrue(s.VisualProxy != null, "the proxy must survive dismemberment");
        }

        [UnityTest]
        public IEnumerator DeactivatingALimb_PrunesItWithoutThrowing()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 4, new Vector3(0f, 4f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile());

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return FixedSteps(10);

            Rigidbody[] bodies = rig.GetComponentsInChildren<Rigidbody>();
            bodies[bodies.Length - 1].gameObject.SetActive(false);

            yield return FixedSteps(10);
            Assert.IsTrue(s.VisualProxy != null);
        }

        // ------------------------------------------------------------------ configuration

        [UnityTest]
        public IEnumerator ExcludedBone_FollowsPhysicsUnstepped()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 4f, 0f));
            Rigidbody[] bodies = rig.GetComponentsInChildren<Rigidbody>();

            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(stepRate: 2f));
            s.BoneTunings = new[] { new BoneTuning { Bone = bodies[1].transform, Exclude = true } };

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return FixedSteps(40);

            Transform excludedProxyBone = null;
            foreach (Transform t in s.VisualProxy.GetComponentsInChildren<Transform>())
                if (t.name == bodies[1].name) { excludedProxyBone = t; break; }

            Assert.IsNotNull(excludedProxyBone, "could not locate the excluded bone on the proxy");
            Assert.Less(Quaternion.Angle(bodies[1].rotation, excludedProxyBone.rotation), 1.5f,
                "an excluded bone must track physics continuously, not on the step cadence");
        }

        [UnityTest]
        public IEnumerator ProfileDisabled_StopsSteppingWithoutThrowing()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 4f, 0f));
            OnTwosProfile profile = RigFactory.CreateProfile();
            RagdollStepper s = Spawn(rig, profile);

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return FixedSteps(10);

            profile.Global.Enabled = false;
            yield return FixedSteps(20);

            Assert.IsTrue(s.VisualProxy != null, "the master switch must not destabilise anything");
        }

        [UnityTest]
        public IEnumerator VisibilityCulling_KeepsStateCoherentWhileOffScreen()
        {
            // The schedulers keep running while culled, so there must be no pop when the
            // proxy comes back — the pose applied on return is the up-to-date one.
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 4f, 0f));
            RagdollStepper s = Spawn(rig, RigFactory.CreateProfile(stepRate: 24f));
            s.EnableVisibilityCulling = true;

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return FixedSteps(30);

            Assert.DoesNotThrow(() => { var _ = s.IsSettled; });
            Assert.IsTrue(s.VisualProxy != null,
                "culling must skip writes only, never disturb scheduler state");
        }

        [UnityTest]
        public IEnumerator NoProfile_FallsBackToInspectorFieldsWithoutThrowing()
        {
            RigFactory.CreateFloor(_scope);
            GameObject rig = RigFactory.CreateJointedChain(_scope, 3, new Vector3(0f, 4f, 0f));
            var s = rig.AddComponent<RagdollStepper>();
            s.Profile = null;   // every Resolve* helper must cope

            yield return null;
            _scope.Track(s.VisualProxy);
            yield return FixedSteps(30);

            Assert.IsTrue(s.VisualProxy != null);
        }
    }
}
