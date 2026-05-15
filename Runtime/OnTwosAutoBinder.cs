using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Best-effort heuristics for discovering rig roots on a GameObject.
    /// Used by <see cref="OnTwosAuthoring"/> when its slots are left empty.
    /// All methods are safe to call with a null argument.
    /// </summary>
    public static class OnTwosAutoBinder
    {
        /// <summary>
        /// Find the first Animator anywhere in the hierarchy under <paramref name="root"/>.
        /// Returns null if none exists.
        /// </summary>
        public static Animator FindAnimator(Transform root)
        {
            if (root == null) return null;
            return root.GetComponentInChildren<Animator>(true);
        }

        /// <summary>
        /// Find the bone root for <see cref="AnimationStepper"/>.
        ///
        /// For humanoid avatars, returns the Hips bone so that root-motion
        /// transforms are not included in the stepped set. For all other rigs
        /// (generic, non-humanoid, or no Animator), returns the Animator's
        /// own transform, which is the natural root of the driven hierarchy.
        /// If no Animator is provided, falls back to <paramref name="sourceRoot"/>.
        ///
        /// This is a heuristic — assign BoneRoot manually if the result is wrong
        /// for your rig.
        /// </summary>
        public static Transform FindBoneRoot(Transform sourceRoot, Animator animator)
        {
            if (animator == null) return sourceRoot;

            if (animator.isHuman)
            {
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null) return hips;
            }

            // Generic / non-humanoid rigs: the Animator's transform is the root
            // of whatever hierarchy the controller drives.
            return animator.transform;
        }

        /// <summary>
        /// Find the physics root for <see cref="RagdollStepper"/>.
        ///
        /// Returns the deepest ancestor of <paramref name="sourceRoot"/> that
        /// contains every <see cref="Rigidbody"/> in the hierarchy. Works for
        /// any physics setup: joint ragdolls, single free-body objects, compound
        /// colliders, articulation bodies, or anything else that uses Rigidbodies.
        ///
        /// Falls back to <paramref name="sourceRoot"/> if no Rigidbodies are found
        /// or the ancestor search cannot resolve cheaply.
        /// </summary>
        public static Transform FindPhysicsRoot(Transform sourceRoot)
        {
            if (sourceRoot == null) return null;
            Rigidbody[] bodies = sourceRoot.GetComponentsInChildren<Rigidbody>(true);
            if (bodies.Length == 0) return sourceRoot;

            // Walk up from the first body until the candidate contains every body.
            Transform candidate = bodies[0].transform.parent ?? sourceRoot;
            while (candidate != null && candidate != sourceRoot.parent)
            {
                bool containsAll = true;
                for (int i = 1; i < bodies.Length; i++)
                {
                    if (!bodies[i].transform.IsChildOf(candidate))
                    { containsAll = false; break; }
                }
                if (containsAll) return candidate;
                candidate = candidate.parent;
            }
            return sourceRoot;
        }

        /// <summary>
        /// Returns true if <paramref name="root"/> contains at least one
        /// <see cref="Rigidbody"/> anywhere in its hierarchy. Used to validate
        /// that <see cref="RagdollStepper"/> will have something to track.
        /// Works for joint ragdolls, single rigid bodies, compound colliders —
        /// any physics-driven object.
        /// </summary>
        public static bool HasPhysicsBodies(Transform root)
        {
            if (root == null) return false;
            return root.GetComponentInChildren<Rigidbody>(true) != null;
        }
    }
}