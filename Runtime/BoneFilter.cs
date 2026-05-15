namespace OnTwos.Runtime.Utilities
{
    using UnityEngine;

    /// <summary>
    /// Tests whether a bone should be excluded from stepping based on its name.
    ///
    /// Some bones are better left at their full-resolution animator pose rather
    /// than being stepped — for example, end-effectors that clip geometry when
    /// held, IK targets that must track a target continuously, or attach points
    /// that need to stay in sync with game logic. Use <see cref="OnTwosProfile"/>
    /// <c>ExcludeKeywords</c> and <c>BoneOverrides</c> to specify these for your rig.
    /// </summary>
    public static class BoneFilter
    {
        /// <summary>
        /// Returns true if <paramref name="bone"/> should be excluded from stepping.
        /// Checks direct references first, then falls back to keyword matching.
        /// </summary>
        public static bool IsExcluded(Transform bone, Transform[] excludeBones, string[] excludeKeywords)
        {
            // Direct reference check — unambiguous
            if (excludeBones != null)
                for (int i = 0; i < excludeBones.Length; i++)
                    if (excludeBones[i] == bone) return true;

            // Keyword match — useful for bulk exclusion by naming convention
            if (string.IsNullOrEmpty(bone.name)) return false;
            if (excludeKeywords == null || excludeKeywords.Length == 0) return false;

            string lower = bone.name.ToLowerInvariant();
            for (int i = 0; i < excludeKeywords.Length; i++)
            {
                string kw = excludeKeywords[i];
                if (!string.IsNullOrEmpty(kw) && lower.Contains(kw.ToLowerInvariant()))
                    return true;
            }
            return false;
        }
    }
}