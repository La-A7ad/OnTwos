using UnityEngine;

namespace OnTwos.Runtime.Utilities
{
    /// <summary>
    /// Resolves the bone rules — exclusion, per-bone tau, per-bone response curve —
    /// into index-parallel arrays once, then hands them out for free every frame.
    ///
    /// Why this exists: the steppers used to call <see cref="BoneFilter"/> per bone per
    /// frame, and every one of those calls read <c>Transform.name</c> (a fresh managed
    /// string from the native side) and ran <c>ToLowerInvariant()</c> on it and on every
    /// keyword. On a 60-bone rig at 60fps that is thousands of string allocations per
    /// second to recompute an answer that almost never changes.
    ///
    /// The rules only change when someone edits the profile, so <see cref="Sync"/> does
    /// a cheap reference-level comparison each frame and re-resolves only on a real
    /// edit. Live tuning in Play mode keeps working; the per-frame cost drops to a
    /// handful of reference compares.
    ///
    /// Precedence, most specific first:
    ///   1. <see cref="BoneTuning"/>   — direct Transform reference, per rig
    ///   2. <see cref="OnTwosProfile.BoneOverride"/> — name substring, per profile
    ///   3. explicit exclude-bone Transform references
    ///   4. <c>ExcludeKeywords</c>     — name substring, per profile
    /// </summary>
    public sealed class BoneRuleSet
    {
        // Resolved, index-parallel to the bones passed to Resolve().
        public bool[] Excluded { get; private set; }
        public float[] TauOverride { get; private set; }
        public AnimationCurve[] ResponseCurve { get; private set; }

        // Sources, kept for change detection only.
        private Transform[] _bones;
        private Transform[] _excludeBones;
        private string[] _keywords;
        private OnTwosProfile.BoneOverride[] _overrides;
        private BoneTuning[] _tunings;

        // Lowercased copies, rebuilt only when the sources change.
        private string[] _loweredKeywords;
        private string[] _loweredOverrideNames;
        private string[] _loweredBoneNames;

        // Raw snapshots of the strings behind the lowered caches, so an in-place edit
        // to a keyword (rather than a whole-array replacement) is still detected.
        private string[] _rawKeywords;
        private string[] _rawOverrideNames;

        /// <summary>
        /// Number of bones currently resolved. Zero until the first Sync.
        /// </summary>
        public int Count => Excluded?.Length ?? 0;

        /// <summary>
        /// Re-resolve if anything changed since the last call, otherwise do nothing.
        /// Safe and cheap to call every frame. Returns true when a re-resolve happened.
        /// </summary>
        public bool Sync(
            Transform[] bones,
            Transform[] excludeBones,
            string[] keywords,
            OnTwosProfile.BoneOverride[] overrides,
            BoneTuning[] tunings)
        {
            bool bonesChanged = !ReferenceEquals(_bones, bones) ||
                                Excluded == null ||
                                (bones != null && Excluded.Length != bones.Length);

            bool rulesChanged = bonesChanged
                                || !ReferenceEquals(_excludeBones, excludeBones)
                                || !ReferenceEquals(_tunings, tunings)
                                || KeywordsChanged(keywords)
                                || OverridesChanged(overrides);

            if (!rulesChanged) return false;

            _bones        = bones;
            _excludeBones = excludeBones;
            _keywords     = keywords;
            _overrides    = overrides;
            _tunings      = tunings;

            if (bonesChanged) CacheBoneNames(bones);
            CacheLoweredRules(keywords, overrides);
            Resolve();
            return true;
        }

        // ------------------------------------------------------------------ change detection

        private bool KeywordsChanged(string[] keywords)
        {
            if (!ReferenceEquals(_keywords, keywords)) return true;
            if (keywords == null) return false;
            if (_rawKeywords == null || _rawKeywords.Length != keywords.Length) return true;

            // Reference compare, not string compare: an unedited field hands back the
            // same interned instance, and a genuine edit produces a new one.
            for (int i = 0; i < keywords.Length; i++)
                if (!ReferenceEquals(_rawKeywords[i], keywords[i])) return true;

            return false;
        }

        private bool OverridesChanged(OnTwosProfile.BoneOverride[] overrides)
        {
            if (!ReferenceEquals(_overrides, overrides)) return true;
            if (overrides == null) return false;
            if (_rawOverrideNames == null || _rawOverrideNames.Length != overrides.Length) return true;

            for (int i = 0; i < overrides.Length; i++)
            {
                string name = overrides[i]?.NameContains;
                if (!ReferenceEquals(_rawOverrideNames[i], name)) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------ caching

        private void CacheBoneNames(Transform[] bones)
        {
            int n = bones?.Length ?? 0;
            if (_loweredBoneNames == null || _loweredBoneNames.Length != n)
                _loweredBoneNames = new string[n];

            for (int i = 0; i < n; i++)
            {
                Transform b = bones[i];
                _loweredBoneNames[i] = b == null || string.IsNullOrEmpty(b.name)
                    ? string.Empty
                    : b.name.ToLowerInvariant();
            }
        }

        private void CacheLoweredRules(string[] keywords, OnTwosProfile.BoneOverride[] overrides)
        {
            int kn = keywords?.Length ?? 0;
            if (_loweredKeywords == null || _loweredKeywords.Length != kn)
            {
                _loweredKeywords = new string[kn];
                _rawKeywords     = new string[kn];
            }
            for (int i = 0; i < kn; i++)
            {
                string kw = keywords[i];
                _rawKeywords[i]     = kw;
                _loweredKeywords[i] = string.IsNullOrEmpty(kw) ? null : kw.ToLowerInvariant();
            }

            int on = overrides?.Length ?? 0;
            if (_loweredOverrideNames == null || _loweredOverrideNames.Length != on)
            {
                _loweredOverrideNames = new string[on];
                _rawOverrideNames     = new string[on];
            }
            for (int i = 0; i < on; i++)
            {
                string name = overrides[i]?.NameContains;
                _rawOverrideNames[i]     = name;
                _loweredOverrideNames[i] = string.IsNullOrEmpty(name) ? null : name.ToLowerInvariant();
            }
        }

        // ------------------------------------------------------------------ resolution

        private void Resolve()
        {
            int n = _bones?.Length ?? 0;

            if (Excluded == null || Excluded.Length != n)
            {
                Excluded      = new bool[n];
                TauOverride   = new float[n];
                ResponseCurve = new AnimationCurve[n];
            }

            for (int i = 0; i < n; i++)
            {
                Transform bone = _bones[i];
                Excluded[i]      = false;
                TauOverride[i]   = 0f;
                ResponseCurve[i] = null;

                if (bone == null) { Excluded[i] = true; continue; }

                // 1. BoneTuning — direct reference, highest precedence and fully
                //    self-describing, so a match here settles every field at once.
                BoneTuning tuning = FindTuning(bone);
                if (tuning != null)
                {
                    Excluded[i]      = tuning.Exclude;
                    TauOverride[i]   = tuning.TauOverride > 0f ? tuning.TauOverride : 0f;
                    ResponseCurve[i] = tuning.HasResponseCurve ? tuning.ResponseCurveOverride : null;
                    continue;
                }

                string lower = _loweredBoneNames[i];

                // 2. BoneOverride — first name match wins and stops the search, so a
                //    bone claimed by an override is never re-tested against keywords.
                int match = FindOverride(lower);
                if (match >= 0)
                {
                    var o = _overrides[match];
                    Excluded[i]    = o.ForceExclude;
                    TauOverride[i] = o.TauOverride > 0f ? o.TauOverride : 0f;
                    continue;
                }

                // 3. Explicit Transform exclusions.
                if (_excludeBones != null)
                {
                    bool hit = false;
                    for (int k = 0; k < _excludeBones.Length; k++)
                        if (_excludeBones[k] == bone) { hit = true; break; }
                    if (hit) { Excluded[i] = true; continue; }
                }

                // 4. Keywords.
                if (lower.Length == 0 || _loweredKeywords == null) continue;
                for (int k = 0; k < _loweredKeywords.Length; k++)
                {
                    string kw = _loweredKeywords[k];
                    if (kw != null && lower.Contains(kw)) { Excluded[i] = true; break; }
                }
            }
        }

        private BoneTuning FindTuning(Transform bone)
        {
            if (_tunings == null) return null;
            for (int i = 0; i < _tunings.Length; i++)
            {
                BoneTuning t = _tunings[i];
                if (t != null && t.Bone == bone) return t;
            }
            return null;
        }

        private int FindOverride(string loweredBoneName)
        {
            if (_overrides == null || loweredBoneName.Length == 0) return -1;
            for (int i = 0; i < _loweredOverrideNames.Length; i++)
            {
                string name = _loweredOverrideNames[i];
                if (name != null && loweredBoneName.Contains(name)) return i;
            }
            return -1;
        }
    }
}
