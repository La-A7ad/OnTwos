using System.Collections.Generic;
using UnityEngine;

namespace CrunchyRagdoll.Runtime.Utilities
{
    /// <summary>
    /// Caches the "root → leaf" name path for every transform under a given root,
    /// enabling O(1) lookup of "the equivalent bone in this other hierarchy."
    ///
    /// Used to map source-rig Rigidbodies to their counterparts in the cloned
    /// visual proxy: both hierarchies share identical structure right after
    /// Instantiate, so the path string is a stable cross-reference even when
    /// transform references diverge.
    /// </summary>
    public sealed class BonePathCache
    {
        private readonly Dictionary<string, Transform> _pathToTransform;
        private readonly Transform _root;

        public Transform Root => _root;
        public int Count => _pathToTransform.Count;

        public BonePathCache(Transform root, bool includeInactive = true)
        {
            _root = root;
            _pathToTransform = new Dictionary<string, Transform>(64);

            if (root == null) return;

            Transform[] all = root.GetComponentsInChildren<Transform>(includeInactive);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;

                string path = GetPath(root, t);
                // Last writer wins on collision — duplicate names in sibling positions
                // are caller's problem; we just store the most recently seen.
                _pathToTransform[path] = t;
            }
        }

        public bool TryGet(string path, out Transform transform)
            => _pathToTransform.TryGetValue(path, out transform);

        /// <summary>
        /// Find <paramref name="otherTransform"/>'s counterpart in this cache by
        /// reconstructing its path under <paramref name="otherRoot"/> and looking
        /// up the matching entry. Returns null on miss.
        /// </summary>
        public Transform Find(Transform otherRoot, Transform otherTransform)
        {
            string path = GetPath(otherRoot, otherTransform);
            return _pathToTransform.TryGetValue(path, out Transform t) ? t : null;
        }

        /// <summary>
        /// Build a slash-separated name path from <paramref name="root"/> down to
        /// <paramref name="target"/>. Empty string if target == root or target is
        /// not actually under root.
        /// </summary>
        public static string GetPath(Transform root, Transform target)
        {
            if (root == null || target == null) return string.Empty;
            if (root == target) return string.Empty;

            var stack = new Stack<string>(8);
            Transform current = target;
            while (current != null && current != root)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            if (current == null) return string.Empty; // target wasn't under root

            return string.Join("/", stack.ToArray());
        }
    }
}
