using CrunchyRagdoll.Runtime.Math;
using CrunchyRagdoll.Runtime.Utilities;
using UnityEngine;

namespace CrunchyRagdoll.Runtime
{
    /// <summary>
    /// Reads animator-driven bone rotations each LateUpdate, feeds them through
    /// the PCHIP + arc-length hold scheduler, and writes back the stepped pose.
    ///
    /// Detached from any specific game's enemy lifecycle: attach this directly
    /// to a rig root, or let CrunchyRagdollAuthoring add it on Awake.
    ///
    /// Notes vs an earlier higher-tau version:
    ///   - Default tau lowered to 5° — reduces foot/ground clipping and gives the
    ///     stepper enough resolution to not miss fast rotations.
    ///   - ExcludeKeywords: bones whose name contains any of these strings stay
    ///     smooth. Default excludes foot/toe bones which clip into the ground
    ///     when their rotation is held while the root moves.
    ///   - Animator state flush: when the Animator transitions to a new state
    ///     (e.g. idle → jump-attack), all held poses are reset immediately to
    ///     the current bone rotation. Prevents the rig visually facing the
    ///     wrong direction when the new state fires.
    /// </summary>
    [AddComponentMenu("CrunchyRagdoll/Animation Stepper")]
    public sealed class AnimationStepper : MonoBehaviour, ICrunchyComponent
    {
        [Tooltip("Optional profile. If set, Tau / CandidatesPerSegment / ExcludeKeywords " +
                 "are read live from this every frame; the local fields are used as " +
                 "fallback when no profile is assigned.")]
        public CrunchyRagdollProfile Profile;

        [Tooltip("Where to start searching for bones. Falls back to this transform.")]
        public Transform BoneRoot;

        [Tooltip("Animator to watch for state transitions. Auto-discovered if null.")]
        public Animator AnimatorRoot;

        [Header("Fallback settings (used when Profile is null)")]
        [Range(0.5f, 45f)] public float Tau = 5f;
        [Range(1, 4)] public int CandidatesPerSegment = 2;
        public string[] ExcludeKeywords = { "foot", "toe", "heel" };

        private Transform[] _bones;
        private HoldFrameScheduler[] _schedulers;
        private bool[] _excluded;
        private float _startTime;
        private bool _ready;

        private AnimatorStateWatcher _stateWatcher;

        private void Start()
        {
            Transform searchRoot = BoneRoot != null ? BoneRoot : transform;
            _bones = searchRoot.GetComponentsInChildren<Transform>(true);
            _schedulers = new HoldFrameScheduler[_bones.Length];
            _excluded = new bool[_bones.Length];

            if (AnimatorRoot == null)
                AnimatorRoot = GetComponentInChildren<Animator>();
            _stateWatcher = new AnimatorStateWatcher(AnimatorRoot);

            float tau = ResolveTau();
            int candidates = ResolveCandidates();
            string[] excludes = ResolveExcludeKeywords();

            for (int i = 0; i < _bones.Length; i++)
            {
                _excluded[i] = BoneFilter.IsExcluded(_bones[i].name, excludes);
                _schedulers[i] = _excluded[i]
                    ? null
                    : new HoldFrameScheduler(tau, candidates);
            }

            _startTime = Time.time;
            _ready = true;

            int active = 0;
            foreach (var e in _excluded) if (!e) active++;
            Debug.Log($"[CrunchyRagdoll/AnimationStepper] {gameObject.name} — " +
                      $"{active}/{_bones.Length} bones active, τ={tau}°, n={candidates}");
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (Profile != null && !Profile.Global.Enabled) return;

            float t = Time.time - _startTime;
            float liveTau = ResolveTau();

            if (_stateWatcher.IsValid && _stateWatcher.Poll())
                FlushAllHolds();

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null || _excluded[i]) continue;

                // Sync tau before each update so live profile edits take effect
                // immediately, not just on the values captured at Start.
                _schedulers[i].Tau = liveTau;

                Quaternion held = _schedulers[i].Update(t, _bones[i].localRotation);
                _bones[i].localRotation = held;
            }
        }

        /// <summary>
        /// Reset every scheduler to the bone's current rotation. Called on
        /// Animator state transitions so new states start clean.
        /// </summary>
        public void FlushAllHolds()
        {
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null || _excluded[i]) continue;
                _schedulers[i].Reset(_bones[i].localRotation);
            }
        }

        public void Deactivate()
        {
            enabled = false;
        }

        // -------------- profile-or-fallback resolvers --------------

        private float ResolveTau()
            => Profile != null ? Profile.LiveAnimation.AnimTau : Tau;

        private int ResolveCandidates()
            => Profile != null ? Profile.LiveAnimation.GaussPoints : CandidatesPerSegment;

        private string[] ResolveExcludeKeywords()
            => Profile != null ? Profile.LiveAnimation.ExcludeKeywords : ExcludeKeywords;
    }
}
