namespace OnTwos.Runtime.Utilities
{
    using UnityEngine;

    /// <summary>
    /// Tests whether a bone should be excluded from stepping based on its name,
    /// and resolves per-bone tau overrides from the profile's BoneOverrides list.
    ///
    /// BoneOverrides take precedence over ExcludeKeywords: if a bone matches any
    /// BoneOverride entry it is handled entirely by that entry (ForceExclude decides
    /// exclusion; TauOverride > 0 provides a per-bone threshold). Keywords are only
    /// evaluated when no BoneOverride matches.
    /// </summary>
    public static class BoneFilter
    {
        /// <summary>
        /// Returns true if <paramref name="bone"/> should be excluded from stepping.
        ///
        /// FIX (Bug E): overrides parameter wires the profile's BoneOverrides list.
        /// Previously this parameter didn't exist and BoneOverrides had no runtime
        /// effect — bones matched by a BoneOverride with ForceExclude=true were still
        /// stepped, and per-bone TauOverride was silently ignored.
        ///
        /// Precedence: BoneOverrides > direct Transform references > ExcludeKeywords.
        /// A bone that matches a BoneOverride entry without ForceExclude is explicitly
        /// not excluded, even if its name also matches an ExcludeKeyword.
        /// </summary>
        public static bool IsExcluded(
            Transform bone,
            Transform[] excludeBones,
            string[] excludeKeywords,
            OnTwosProfile.BoneOverride[] overrides = null)
        {
            if (bone == null) return false;

            string lower = string.IsNullOrEmpty(bone.name)
                ? string.Empty
                : bone.name.ToLowerInvariant();

            // BoneOverrides take precedence. The first match wins.
            if (overrides != null && lower.Length > 0)
            {
                for (int i = 0; i < overrides.Length; i++)
                {
                    var o = overrides[i];
                    if (string.IsNullOrEmpty(o.NameContains)) continue;
                    if (!lower.Contains(o.NameContains.ToLowerInvariant())) continue;

                    // Matched an override: ForceExclude decides fate.
                    // Whether or not it's excluded, we stop here — keywords don't apply.
                    return o.ForceExclude;
                }
            }

            // Direct Transform reference check — unambiguous, no string allocation.
            if (excludeBones != null)
                for (int i = 0; i < excludeBones.Length; i++)
                    if (excludeBones[i] == bone) return true;

            // Keyword match — useful for bulk exclusion by naming convention.
            if (lower.Length == 0) return false;
            if (excludeKeywords == null || excludeKeywords.Length == 0) return false;

            for (int i = 0; i < excludeKeywords.Length; i++)
            {
                string kw = excludeKeywords[i];
                if (!string.IsNullOrEmpty(kw) && lower.Contains(kw.ToLowerInvariant()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the TauOverride for <paramref name="bone"/> from the profile's
        /// BoneOverrides list, or 0 if no override is configured for this bone.
        /// A return value of 0 means "use the global profile tau".
        ///
        /// FIX (Bug E): exposes per-bone tau overrides to callers (AnimationStepper)
        /// so individual schedulers can be seeded with bone-specific thresholds.
        /// </summary>
        public static float GetTauOverride(
            Transform bone,
            OnTwosProfile.BoneOverride[] overrides)
        {
            if (bone == null || overrides == null || string.IsNullOrEmpty(bone.name))
                return 0f;

            string lower = bone.name.ToLowerInvariant();
            for (int i = 0; i < overrides.Length; i++)
            {
                var o = overrides[i];
                if (string.IsNullOrEmpty(o.NameContains)) continue;
                if (!lower.Contains(o.NameContains.ToLowerInvariant())) continue;

                // First match wins; 0 or negative means no override.
                return o.TauOverride > 0f ? o.TauOverride : 0f;
            }
            return 0f;
        }
    }
}