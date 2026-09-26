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

        // Value snapshots of the rule data, so an in-place inspector edit is detected.
        //
        // Reference comparison alone is not enough and used to be all there was. Unity's
        // inspector mutates the EXISTING element when a field inside a list entry is
        // edited — only adding or removing an entry replaces the array — so editing a
        // TauOverride or ticking an Exclude box left every reference identical and
        // Resolve() never ran. DOCUMENTATION.md section 9 promises these re-resolve as
        // soon as they are edited, and until now only a renamed keyword or a resized list
        // actually did.
        private bool[]  _rawOverrideExclude;
        private float[] _rawOverrideTau;

        private Transform[]      _rawTuningBones;
        private bool[]           _rawTuningExclude;
        private float[]          _rawTuningTau;
        private AnimationCurve[] _rawTuningCurves;
        private int[]            _rawTuningCurveLengths;

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
                                || TuningsChanged(tunings)
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
            CacheTuningValues(tunings);
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

            if (_rawOverrideExclude == null || _rawOverrideExclude.Length != overrides.Length) return true;

            for (int i = 0; i < overrides.Length; i++)
            {
                OnTwosProfile.BoneOverride o = overrides[i];

                if (!ReferenceEquals(_rawOverrideNames[i], o?.NameContains)) return true;

                // Values, not just the name. Toggling ForceExclude or nudging a per-bone
                // tau leaves NameContains identical, and comparing only the name meant
                // neither took effect until the list was resized.
                if (_rawOverrideExclude[i] != (o != null && o.ForceExclude)) return true;
                if (_rawOverrideTau[i]     != (o?.TauOverride ?? 0f)) return true;
            }

            return false;
        }

        /// <summary>
        /// True when the tuning list, or any value inside it, differs from the snapshot.
        ///
        /// The response curve is compared by reference and key count rather than by
        /// contents. That is deliberate and sufficient: <see cref="ResponseCurve"/> stores
        /// the curve INSTANCE, so editing its keys in place is already reflected the next
        /// time it is evaluated and needs no re-resolve. What does need one is swapping in
        /// a different curve, or a curve crossing between empty and non-empty — an
        /// unauthored curve evaluates to zero for every input and must read as "not set"
        /// rather than as a curve that drives tau to nothing.
        /// </summary>
        private bool TuningsChanged(BoneTuning[] tunings)
        {
            if (!ReferenceEquals(_tunings, tunings)) return true;
            if (tunings == null) return false;
            if (_rawTuningBones == null || _rawTuningBones.Length != tunings.Length) return true;

            for (int i = 0; i < tunings.Length; i++)
            {
                BoneTuning t = tunings[i];

                if (!ReferenceEquals(_rawTuningBones[i], t?.Bone)) return true;
                if (_rawTuningExclude[i] != (t != null && t.Exclude)) return true;
                if (_rawTuningTau[i]     != (t?.TauOverride ?? 0f)) return true;

                AnimationCurve curve = t?.ResponseCurveOverride;
                if (!ReferenceEquals(_rawTuningCurves[i], curve)) return true;
                if (_rawTuningCurveLengths[i] != (curve?.length ?? 0)) return true;
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
                _rawOverrideExclude   = new bool[on];
                _rawOverrideTau       = new float[on];
            }
            for (int i = 0; i < on; i++)
            {
                OnTwosProfile.BoneOverride o = overrides[i];
                string name = o?.NameContains;

                _rawOverrideNames[i]     = name;
                _rawOverrideExclude[i]   = o != null && o.ForceExclude;
                _rawOverrideTau[i]       = o?.TauOverride ?? 0f;
                _loweredOverrideNames[i] = string.IsNullOrEmpty(name) ? null : name.ToLowerInvariant();
            }
        }

        // Snapshot the tuning values that TuningsChanged compares against. Allocates only
        // when the list is resized, which is an authoring action, never a per-frame one.
        private void CacheTuningValues(BoneTuning[] tunings)
        {
            int n = tunings?.Length ?? 0;
            if (_rawTuningBones == null || _rawTuningBones.Length != n)
            {
                _rawTuningBones        = new Transform[n];
                _rawTuningExclude      = new bool[n];
                _rawTuningTau          = new float[n];
                _rawTuningCurves       = new AnimationCurve[n];
                _rawTuningCurveLengths = new int[n];
            }

            for (int i = 0; i < n; i++)
            {
                BoneTuning t = tunings[i];
                AnimationCurve curve = t?.ResponseCurveOverride;

                _rawTuningBones[i]        = t?.Bone;
                _rawTuningExclude[i]      = t != null && t.Exclude;
                _rawTuningTau[i]          = t?.TauOverride ?? 0f;
                _rawTuningCurves[i]       = curve;
                _rawTuningCurveLengths[i] = curve?.length ?? 0;
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
