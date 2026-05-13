using UnityEngine;

namespace CrunchyRagdoll.Runtime
{
    /// <summary>
    /// Best-effort heuristics for discovering rig roots on a prefab.
    /// Used by <see cref="CrunchyRagdollAuthoring"/> when its slots are left empty.
    /// </summary>
    public static class CrunchyRagdollAutoBinder
    {
        /// <summary>
        /// Find the first Animator under root. Returns null if none exist.
        /// </summary>
        public static Animator FindAnimator(Transform root)
        {
            if (root == null) return null;
            return root.GetComponentInChildren<Animator>(true);
        }

        /// <summary>
        /// Find the bone root. Prefers the Animator's avatar root bone; falls
        /// back to the Animator transform; falls back to the source root.
        /// </summary>
        public static Transform FindBoneRoot(Transform sourceRoot, Animator animator)
        {
            if (animator != null)
            {
                // The transform Animator drives — usually the rig root for an Avatar.
                if (animator.isHuman && animator.GetBoneTransform(HumanBodyBones.Hips) != null)
                    return animator.GetBoneTransform(HumanBodyBones.Hips);
                return animator.transform;
            }
            return sourceRoot;
        }

        /// <summary>
        /// Find the ragdoll root. Heuristic: the deepest common ancestor of
        /// every Rigidbody under <paramref name="sourceRoot"/>. Falls back to
        /// sourceRoot if no Rigidbodies exist or it can't be computed cheaply.
        /// </summary>
        public static Transform FindRagdollRoot(Transform sourceRoot)
        {
            if (sourceRoot == null) return null;
            Rigidbody[] bodies = sourceRoot.GetComponentsInChildren<Rigidbody>(true);
            if (bodies.Length == 0) return sourceRoot;

            // Walk up from the first body until the ancestor contains every body.
            Transform candidate = bodies[0].transform.parent ?? sourceRoot;
            while (candidate != null && candidate != sourceRoot.parent)
            {
                bool containsAll = true;
                for (int i = 1; i < bodies.Length; i++)
                {
                    if (!bodies[i].transform.IsChildOf(candidate))
                    {
                        containsAll = false;
                        break;
                    }
                }
                if (containsAll) return candidate;
                candidate = candidate.parent;
            }
            return sourceRoot;
        }

        /// <summary>
        /// True if <paramref name="root"/> appears to have a Unity ragdoll
        /// (at least one CharacterJoint or HingeJoint + Rigidbody pair).
        /// </summary>
        public static bool HasRagdoll(Transform root)
        {
            if (root == null) return false;
            Joint[] joints = root.GetComponentsInChildren<Joint>(true);
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] != null &&
                    (joints[i] is CharacterJoint || joints[i] is HingeJoint || joints[i] is ConfigurableJoint))
                    return true;
            }
            return false;
        }
    }
}
