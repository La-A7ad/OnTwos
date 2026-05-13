using System.Collections.Generic;
using UnityEngine;

namespace CrunchyRagdoll.Runtime.Utilities
{
    /// <summary>
    /// Builds and configures the visual proxy GameObject used by RagdollStepper.
    ///
    /// Why a proxy at all: writing localRotation to a non-kinematic Rigidbody's
    /// transform makes CharacterJoint think the bone has drifted from its
    /// constraint target; the solver pushes back, and that corrective impulse
    /// accumulates every FixedUpdate. Bodies that should fall under gravity end
    /// up accelerating sideways. So we leave physics alone, clone the visible
    /// skin into a separate hierarchy at scene root, strip its physics, and
    /// drive its transforms from snapshots of the live physics rig.
    /// </summary>
    public static class RagdollProxyBuilder
    {
        public struct BuildResult
        {
            public GameObject Proxy;
            public Transform[] VisualBones;   // proxy bones, parallel to SourceBodies
            public Rigidbody[] SourceBodies;  // tracked source bodies
        }

        /// <summary>
        /// Clone <paramref name="source"/>, parent the clone to scene root,
        /// strip its dynamic components (or not, per <paramref name="stripComponents"/>),
        /// and pair every source Rigidbody with the path-equivalent transform on
        /// the proxy.
        ///
        /// The proxy is intentionally parented to NULL (scene root). If it were a
        /// child of the source, anything that disables or destroys the source —
        /// pooling, hit reactions, dismemberment systems — would take the proxy
        /// with it. Scene-root means the proxy outlives the source and lifetime
        /// is controlled explicitly by the caller (usually OnDestroy on RagdollStepper).
        /// </summary>
        public static BuildResult Build(GameObject source,
                                        bool stripComponents,
                                        bool forceEnableRenderers,
                                        string proxyNameSuffix = " [CrunchProxy]")
        {
            BuildResult result = default;
            if (source == null) return result;

            GameObject clone = Object.Instantiate(source,
                source.transform.position, source.transform.rotation, null);
            clone.name = source.name + proxyNameSuffix;
            clone.SetActive(false);

            // Kill all CrunchyRagdoll-authored components on the clone BEFORE
            // it goes active. Instantiate copies all components including ours,
            // so without this the clone would also try to build a proxy → infinite
            // recursion (mitigated only by Destroy()'s end-of-frame timing, but a
            // few-frame cascade is still possible). DestroyImmediate on an inactive
            // object is safe and has no frame-ordering side effects.
            DestroyImmediateAllOfType<MonoBehaviour>(clone, m => m is ICrunchyComponent);

            if (stripComponents)
                StripToRenderersOnly(clone);

            clone.SetActive(true);

            if (forceEnableRenderers)
            {
                // Some game scripts disable renderers in their OnDestroy as part
                // of cleanup. If those scripts were stripped via DestroyImmediate
                // above, they may have run their OnDisable/OnDestroy callbacks
                // and left renderers disabled. Force-enable everything.
                Renderer[] renderers = clone.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null) continue;
                    renderers[i].enabled = true;
                    if (renderers[i] is SkinnedMeshRenderer smr)
                        smr.updateWhenOffscreen = true;
                }
            }

            result.Proxy = clone;

            // Build the path-based bone mapping.
            Rigidbody[] sourceBodies = source.GetComponentsInChildren<Rigidbody>(true);
            BonePathCache proxyPaths = new BonePathCache(clone.transform);
            Transform sourceRoot = source.transform;

            List<Rigidbody> bodyList = new List<Rigidbody>(sourceBodies.Length);
            List<Transform> boneList = new List<Transform>(sourceBodies.Length);

            for (int i = 0; i < sourceBodies.Length; i++)
            {
                Rigidbody rb = sourceBodies[i];
                if (rb == null) continue;

                Transform proxyBone = proxyPaths.Find(sourceRoot, rb.transform);
                if (proxyBone == null)
                {
                    Debug.LogWarning(
                        $"[CrunchyRagdoll] Missing proxy match for bone path " +
                        $"'{BonePathCache.GetPath(sourceRoot, rb.transform)}'");
                    continue;
                }

                bodyList.Add(rb);
                boneList.Add(proxyBone);
            }

            result.SourceBodies = bodyList.ToArray();
            result.VisualBones = boneList.ToArray();
            return result;
        }

        /// <summary>
        /// Remove physics, behaviours, and animators from the proxy hierarchy.
        /// Joints/Rigidbodies/Colliders are deferred-destroyed so Unity can
        /// resolve the CharacterJoint→Rigidbody dependency ordering itself.
        /// MonoBehaviours/Animators are DestroyImmediate'd while the object
        /// is inactive — preventing any scripts that survived from running
        /// their Start callbacks when SetActive(true) fires.
        /// </summary>
        private static void StripToRenderersOnly(GameObject root)
        {
            Joint[] joints = root.GetComponentsInChildren<Joint>(true);
            for (int i = 0; i < joints.Length; i++)
                if (joints[i] != null) Object.Destroy(joints[i]);

            Rigidbody[] rigidbodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rigidbodies.Length; i++)
                if (rigidbodies[i] != null) Object.Destroy(rigidbodies[i]);

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) Object.Destroy(colliders[i]);

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
                if (behaviours[i] != null) Object.DestroyImmediate(behaviours[i]);

            Animator[] animators = root.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
                if (animators[i] != null) Object.DestroyImmediate(animators[i]);
        }

        private static void DestroyImmediateAllOfType<T>(GameObject root, System.Predicate<T> match)
            where T : Component
        {
            T[] components = root.GetComponentsInChildren<T>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null && match(components[i]))
                    Object.DestroyImmediate(components[i]);
            }
        }

        /// <summary>
        /// Pick the highest-mass body as the "anchor" — usually the hips/torso.
        /// Used as a representative bone for settle and wake checks.
        /// </summary>
        public static int PickAnchorIndex(Rigidbody[] bodies)
        {
            if (bodies == null || bodies.Length == 0) return 0;

            int best = 0;
            float bestMass = bodies[0] != null ? bodies[0].mass : float.MinValue;

            for (int i = 1; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i];
                if (rb == null) continue;
                if (rb.mass > bestMass)
                {
                    bestMass = rb.mass;
                    best = i;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// Marker interface implemented by every CrunchyRagdoll MonoBehaviour so
    /// RagdollProxyBuilder can identify and destroy them on a clone before
    /// they re-Awake and spawn their own proxies.
    /// </summary>
    public interface ICrunchyComponent { }
}
