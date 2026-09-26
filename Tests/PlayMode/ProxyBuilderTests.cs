using System.Collections;
using NUnit.Framework;
using OnTwos.Runtime;
using OnTwos.Runtime.Utilities;
using UnityEngine;
using UnityEngine.TestTools;

namespace OnTwos.Tests.PlayMode
{
    /// <summary>
    /// The proxy is the architectural claim of the project: physics runs on the source,
    /// stylisation is written to a clone, and the two never mix. These tests pin that
    /// separation and the clone's lifetime independence.
    ///
    /// Build_DoesNotRunGameScriptLifecycleOnTheClone is a regression guard: Instantiate
    /// on an active source used to run the game's own Awake/OnEnable on the clone before
    /// anything was stripped.
    /// </summary>
    public class ProxyBuilderTests
    {
        private RigFactory.Scope _scope;

        [SetUp]
        public void SetUp()
        {
            _scope = new RigFactory.Scope();
            RigFactory.LifecycleProbe.ResetCounts();
        }

        [TearDown]
        public void TearDown() => _scope.Dispose();

        private void TrackProxy(RagdollProxyBuilder.BuildResult r)
        {
            if (r.Proxy != null) _scope.Track(r.Proxy);
        }

        [Test]
        public void Build_NullSource_ReturnsEmptyResultWithoutThrowing()
        {
            RagdollProxyBuilder.BuildResult r = default;
            Assert.DoesNotThrow(() => r = RagdollProxyBuilder.Build(null, true, true));
            Assert.IsNull(r.Proxy);
        }

        [UnityTest]
        public IEnumerator Build_ParentsTheProxyToSceneRoot()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            Assert.IsNotNull(r.Proxy);
            Assert.IsNull(r.Proxy.transform.parent,
                "a proxy parented under the source dies with the source — pooling, " +
                "dismemberment and hit reactions would all take it with them");
        }

        [UnityTest]
        public IEnumerator Build_StripsAllPhysicsFromTheProxy()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 4, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            Assert.IsEmpty(r.Proxy.GetComponentsInChildren<Rigidbody>(true),
                "a Rigidbody on the proxy would re-enter the solver");
            Assert.IsEmpty(r.Proxy.GetComponentsInChildren<Joint>(true));
            Assert.IsEmpty(r.Proxy.GetComponentsInChildren<Collider>(true));
            Assert.IsEmpty(r.Proxy.GetComponentsInChildren<Animator>(true));
        }

        [UnityTest]
        public IEnumerator Build_KeepsRenderersOnTheProxy()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            Assert.IsNotEmpty(r.Proxy.GetComponentsInChildren<Renderer>(true),
                "the proxy is the only thing the player sees");
        }

        [UnityTest]
        public IEnumerator Build_ForceEnableRenderers_ReEnablesDisabledOnes()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            foreach (Renderer rend in source.GetComponentsInChildren<Renderer>(true))
                rend.enabled = false;
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            foreach (Renderer rend in r.Proxy.GetComponentsInChildren<Renderer>(true))
                Assert.IsTrue(rend.enabled, "stripped scripts may have disabled renderers on their way out");
        }

        [UnityTest]
        public IEnumerator Build_PairsEverySourceBodyWithAProxyBone()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 5, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            Assert.AreEqual(5, r.SourceBodies.Length);
            Assert.AreEqual(r.SourceBodies.Length, r.VisualBones.Length,
                "the two arrays are index-parallel by contract");

            for (int i = 0; i < r.VisualBones.Length; i++)
            {
                Assert.IsNotNull(r.VisualBones[i], $"bone {i} failed to pair");
                Assert.IsTrue(r.VisualBones[i].IsChildOf(r.Proxy.transform),
                    $"bone {i} resolved onto the source rig, not the proxy");
                Assert.AreEqual(r.SourceBodies[i].name, r.VisualBones[i].name,
                    "pairing is by hierarchy path, so names must correspond");
            }
        }

        [UnityTest]
        public IEnumerator Build_ProxyBonesAreOrderedParentsBeforeChildren()
        {
            // ApplyHeldPoses writes world-space position and rotation per bone. Writing a
            // parent after its child would drag the child off the pose just written, so
            // the ordering is load-bearing even though nothing states it.
            GameObject source = RigFactory.CreateJointedChain(_scope, 5, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            for (int i = 0; i < r.VisualBones.Length; i++)
                for (int j = i + 1; j < r.VisualBones.Length; j++)
                    Assert.IsFalse(r.VisualBones[i].IsChildOf(r.VisualBones[j]),
                        $"bone {i} is a descendant of bone {j} but is written first");
        }

        [UnityTest]
        public IEnumerator Build_RemovesOnTwosComponentsFromTheProxy()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            source.AddComponent<AnimationStepper>();
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            Assert.IsEmpty(r.Proxy.GetComponentsInChildren<AnimationStepper>(true),
                "an OnTwos component surviving onto the clone would build its own proxy");
        }

        [UnityTest]
        public IEnumerator Build_ProxyOutlivesTheSource()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);

            Object.Destroy(source);
            yield return null;

            Assert.IsTrue(r.Proxy != null, "destroying the source must not take the proxy with it");
        }

        [UnityTest]
        public IEnumerator Build_WithoutStripping_KeepsTheClonesComponents()
        {
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            yield return null;

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, false, true);
            TrackProxy(r);

            Assert.IsNotEmpty(r.Proxy.GetComponentsInChildren<Rigidbody>(true),
                "stripComponents:false must be honoured");
        }

        [UnityTest]
        public IEnumerator Build_DoesNotRunGameScriptLifecycleOnTheClone()
        {
            // Regression guard for a defect fixed on 2026-09-26. Instantiate of an ACTIVE
            // source produces an ACTIVE clone, so every copied MonoBehaviour's Awake and
            // OnEnable used to run before the strip pass — meaning a ragdoll activation
            // silently re-ran the game's own manager registration, event subscription,
            // VFX and audio. The clone is now built inside a deactivated holder.
            GameObject source = RigFactory.CreateJointedChain(_scope, 3, Vector3.zero);
            source.AddComponent<RigFactory.LifecycleProbe>();
            yield return null;

            RigFactory.LifecycleProbe.ResetCounts();

            RagdollProxyBuilder.BuildResult r = RagdollProxyBuilder.Build(source, true, true);
            TrackProxy(r);
            yield return null;

            Assert.AreEqual(0, RigFactory.LifecycleProbe.Awakes,
                $"cloning ran Awake on the game's own scripts " +
                $"({RigFactory.LifecycleProbe.Awakes} times)");
            Assert.AreEqual(0, RigFactory.LifecycleProbe.Enables,
                $"cloning ran OnEnable on the game's own scripts " +
                $"({RigFactory.LifecycleProbe.Enables} times)");
        }
    }
}
