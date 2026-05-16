using System;
using OnTwos.Runtime.Math;
using OnTwos.Runtime.Utilities;
using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Reads bone rotations each LateUpdate, feeds them through the PCHIP +
    /// arc-length hold scheduler, and writes back the stepped pose.
    ///
    /// Two modes controlled by the <see cref="Mode"/> field:
    ///
    ///   AnimatorDriven (default) — requires an Animator in the hierarchy.
    ///   An AnimatorStateWatcher detects state transitions and flushes held
    ///   poses automatically so new states start clean (no cross-state ghosting).
    ///
    ///   AnySource — no Animator required. Reads whatever localRotation the
    ///   bones have each LateUpdate — works with IK rigs, script-driven bones,
    ///   cloth results baked to transforms, motion matching, audio-reactive
    ///   bones, and anything else that writes to bone transforms directly.
    ///   State-transition flushing is unavailable in this mode; call
    ///   FlushAllHolds() manually if your source system has discrete states.
    /// </summary>
    [AddComponentMenu("CrunchyRagdoll/Animation Stepper")]
    public sealed class AnimationStepper : MonoBehaviour, IOnTwosComponent
    {
        // -----------------------------------------------------------------
        // Mode enum
        // -----------------------------------------------------------------

        public enum StepperMode
        {
            /// <summary>
            /// Reads Animator output. Requires an Animator in the hierarchy.
            /// Detects state transitions via AnimatorStateWatcher and flushes
            /// held poses automatically on each transition.
            /// </summary>
            AnimatorDriven,

            /// <summary>
            /// Reads whatever localRotation the bones have each LateUpdate.
            /// Works with any bone-driving system — IK, scripts, cloth,
            /// motion matching, etc. No Animator required.
            /// </summary>
            AnySource
        }

        // -----------------------------------------------------------------
        // Inspector fields
        // -----------------------------------------------------------------

        [Tooltip("AnimatorDriven: reads Animator output and auto-flushes on state transitions. " +
                 "Requires an Animator in the hierarchy.\n\n" +
                 "AnySource: reads whatever localRotation the bones have each frame. " +
                 "Works with IK, scripts, cloth, motion matching — no Animator required.")]
        public StepperMode Mode = StepperMode.AnimatorDriven;

        [Tooltip("Optional profile. If set, Tau / CandidatesPerSegment / ExcludeKeywords " +
                 "are read live from it every frame; the fallback fields below are used " +
                 "when no profile is assigned.")]
        public OnTwosProfile Profile;

        [Tooltip("Where to start searching for bones. Falls back to this transform if null.")]
        public Transform BoneRoot;

        [Tooltip("Animator to watch for state transitions. Only used in AnimatorDriven mode. " +
                 "Auto-discovered at Start if null; ignored entirely in AnySource mode.")]
        public Animator AnimatorRoot;

        [Tooltip("When enabled, bone writes are skipped while every Renderer on this rig is " +
                 "off-screen. The schedulers keep running (bones are still read and state " +
                 "stays coherent), so there is no visible pop when visibility resumes. " +
                 "Disable if you have no Renderers in the bone hierarchy.")]
        public bool EnableVisibilityCulling = false;

        [Header("Fallback settings (used when Profile is null)")]
        [Range(0.5f, 45f)] public float Tau = 5f;
        [Range(1, 4)] public int CandidatesPerSegment = 2;

        public Transform[] ExcludeBones    = Array.Empty<Transform>();
        public string[]    ExcludeKeywords = Array.Empty<string>();

        // -----------------------------------------------------------------
        // Private state
        // -----------------------------------------------------------------

        private Transform[]          _bones;
        private HoldFrameScheduler[] _schedulers;
        private bool[]               _excluded;
        private float                _startTime;
        private bool                 _ready;

        // Null in AnySource mode or when no Animator is found in AnimatorDriven mode.
        private AnimatorStateWatcher _stateWatcher;

        // Cached renderer set for visibility culling. Populated in Start regardless
        // of whether culling is enabled — the cost is one GetComponentsInChildren call,
        // paid once. The per-frame poll only runs when EnableVisibilityCulling is true.
        private Renderer[] _renderers;

        // -----------------------------------------------------------------
        // Lifecycle
        // -----------------------------------------------------------------

        private void Start()
        {
            Transform searchRoot = BoneRoot != null ? BoneRoot : transform;
            _bones      = searchRoot.GetComponentsInChildren<Transform>(true);
            _schedulers = new HoldFrameScheduler[_bones.Length];
            _excluded   = new bool[_bones.Length];

            // AnimatorStateWatcher is only meaningful in AnimatorDriven mode.
            if (Mode == StepperMode.AnimatorDriven)
            {
                if (AnimatorRoot == null)
                    AnimatorRoot = GetComponentInChildren<Animator>();

                if (AnimatorRoot != null)
                {
                    _stateWatcher = new AnimatorStateWatcher(AnimatorRoot);
                }
                else
                {
                    Debug.LogWarning(
                        $"[CrunchyRagdoll/AnimationStepper] {gameObject.name}: " +
                        "Mode is AnimatorDriven but no Animator was found. " +
                        "State-transition flushing is disabled. " +
                        "Add an Animator or switch Mode to AnySource.");
                }
            }

            // Cache renderers once for optional per-frame visibility checks.
            _renderers = GetComponentsInChildren<Renderer>(true);

            float        tau            = ResolveTau();
            int          candidates     = ResolveCandidates();
            Transform[]  excludeBones   = ResolveExcludeBones();
            string[]     excludeKeywords = ResolveExcludeKeywords();

            for (int i = 0; i < _bones.Length; i++)
            {
                _excluded[i]   = BoneFilter.IsExcluded(_bones[i], excludeBones, excludeKeywords);
                _schedulers[i] = _excluded[i] ? null : new HoldFrameScheduler(tau, candidates);
            }

            _startTime = Time.time;
            _ready     = true;

            int active = 0;
            foreach (var e in _excluded) if (!e) active++;
            Debug.Log(
                $"[CrunchyRagdoll/AnimationStepper] {gameObject.name} — " +
                $"{active}/{_bones.Length} bones active, τ={tau}°, n={candidates}, mode={Mode}");
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (Profile != null && !Profile.Global.Enabled) return;

            float t       = Time.time - _startTime;
            float liveTau = ResolveTau();

            // The null check here handles both AnySource mode (_stateWatcher is never
            // created) and AnimatorDriven mode where no Animator was found at Start.
            if (_stateWatcher != null && _stateWatcher.IsValid && _stateWatcher.Poll())
                FlushAllHolds();

            // Compute visibility once before the loop so each bone doesn't repeat the work.
            // When culled, schedulers still run (bones are read, state is updated) but the
            // localRotation write-back is skipped — no pop when the rig comes back on screen.
            bool culled = EnableVisibilityCulling && !IsVisible();

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null || _excluded[i]) continue;

                // Sync tau so live profile slider changes take effect immediately.
                _schedulers[i].Tau = liveTau;

                Quaternion held = _schedulers[i].Update(t, _bones[i].localRotation);
                if (!culled)
                    _bones[i].localRotation = held;
            }
        }

        // -----------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------

        /// <summary>
        /// Reset every scheduler to the bone's current rotation.
        /// Called automatically on Animator state transitions in AnimatorDriven mode.
        /// Call manually in AnySource mode if your source system has discrete states
        /// and you want to prevent cross-state pose ghosting.
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

        // -----------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------

        // Returns true if any Renderer in the bone hierarchy is currently on-screen.
        // Early-exits on the first visible renderer — typical rigs have 1–5 renderers
        // so the loop is negligibly cheap. Matching the approach used in RagdollStepper.
        private bool IsVisible()
        {
            if (_renderers == null) return true;
            for (int i = 0; i < _renderers.Length; i++)
                if (_renderers[i] != null && _renderers[i].isVisible) return true;
            return false;
        }

        // -----------------------------------------------------------------
        // Profile-or-fallback resolvers
        // -----------------------------------------------------------------

        private float ResolveTau()
            => Profile != null ? Profile.LiveAnimation.AnimTau : Tau;

        private int ResolveCandidates()
            => Profile != null ? Profile.LiveAnimation.GaussPoints : CandidatesPerSegment;

        private string[] ResolveExcludeKeywords()
            => Profile != null ? Profile.LiveAnimation.ExcludeKeywords : ExcludeKeywords;

        // ExcludeBones are Transform[] references — scene-object references that cannot
        // be stored in a profile asset. The profile only carries keyword-based exclusion.
        private Transform[] ResolveExcludeBones() => ExcludeBones;
    }
}
