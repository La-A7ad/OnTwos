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

        [Tooltip("Which Animator layer to watch for state transitions. 0 = base layer. " +
         "Increase if your transitions that matter happen on a higher layer.")]
        [Range(0, 7)]
        public int AnimatorLayerIndex = 0;

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

        [Header("Smear (whole-mesh MVP)")]
        [Tooltip("Bone used to derive the whole-mesh smear vector pushed to the " +
                 "_SmearDirection/_SmearStrength shader properties. Assign a bone that " +
                 "actually carries motion — e.g. Hips — not the Armature root, which is " +
                 "usually stationary. Leave null to disable smear.")]
        public Transform SmearReferenceBone;

        [Header("Fallback settings (used when Profile is null)")]
        [Range(0.5f, 45f)] public float Tau = 5f;
        [Range(1, 4)] public int CandidatesPerSegment = 2;

        [Range(1, 30)]
        [Tooltip("Minimum frames between snaps. Set equal to MaxHoldFrames for a locked, " +
                 "metronomic cadence — 2 = animating on twos, 3 = on threes.")]
        public int MinHoldFrames = 2;

        [Range(1, 30)]
        [Tooltip("Maximum frames before a snap is forced regardless of deviation. " +
                 "Set equal to MinHoldFrames for a locked cadence.")]
        public int MaxHoldFrames = 2;

        public Transform[] ExcludeBones    = Array.Empty<Transform>();
        public string[]    ExcludeKeywords = Array.Empty<string>();

        // -----------------------------------------------------------------
        // Private state
        // -----------------------------------------------------------------

        private Transform[]          _bones;
        private HoldFrameScheduler[] _schedulers;
        private Quaternion[]         _rawRotations;
        private bool[]               _excluded;
        private float                _startTime;
        private bool                 _ready;
        private MaterialPropertyBlock _propertyBlock;
        private Vector3 _rootSmearVector;
        private int _smearBoneIndex = -1; // index of SmearReferenceBone within _bones; -1 = disabled

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
            _bones        = searchRoot.GetComponentsInChildren<Transform>(true);
            _schedulers   = new HoldFrameScheduler[_bones.Length];
            _rawRotations = new Quaternion[_bones.Length];
            _excluded     = new bool[_bones.Length];
            _propertyBlock = new MaterialPropertyBlock();

            // AnimatorStateWatcher is only meaningful in AnimatorDriven mode.
            if (Mode == StepperMode.AnimatorDriven)
            {
                if (AnimatorRoot == null)
                    AnimatorRoot = GetComponentInChildren<Animator>();

                if (AnimatorRoot != null)
                {
                    _stateWatcher = new AnimatorStateWatcher(AnimatorRoot, AnimatorLayerIndex);
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

            float       tau            = ResolveTau();
            int         candidates     = ResolveCandidates();
            int         bufferSize     = ResolveBufferSize();
            int         minHold        = ResolveMinHoldFrames();
            int         maxHold        = ResolveMaxHoldFrames(minHold);
            Transform[] excludeBones   = ResolveExcludeBones();
            string[]    excludeKeywords = ResolveExcludeKeywords();
            OnTwosProfile.BoneOverride[] overrides = ResolveBoneOverrides();

            for (int i = 0; i < _bones.Length; i++)
            {
                _excluded[i] = BoneFilter.IsExcluded(_bones[i], excludeBones, excludeKeywords, overrides);
                if (_excluded[i])
                {
                    _schedulers[i] = null;
                    continue;
                }

                float boneTau = ResolveTauForBone(_bones[i], tau, overrides);
                _schedulers[i] = new HoldFrameScheduler(boneTau, candidates, bufferSize);
                _schedulers[i].CandidatesPerSegment = candidates;
                _schedulers[i].MinHoldFrames = minHold;
                _schedulers[i].MaxHoldFrames = maxHold;
                _rawRotations[i] = _bones[i].localRotation;
            }

            if (SmearReferenceBone != null)
            {
                for (int i = 0; i < _bones.Length; i++)
                {
                    if (_bones[i] == SmearReferenceBone) { _smearBoneIndex = i; break; }
                }
                if (_smearBoneIndex < 0)
                    Debug.LogWarning(
                        $"[CrunchyRagdoll/AnimationStepper] {gameObject.name}: " +
                        "SmearReferenceBone is not under BoneRoot. Smear disabled.");
            }

            _startTime = Time.time;
            _ready     = true;

            int active = 0;
            foreach (var e in _excluded) if (!e) active++;
            Debug.Log(
                $"[CrunchyRagdoll/AnimationStepper] {gameObject.name} — " +
                $"{active}/{_bones.Length} bones active, τ={tau}°, n={candidates}, mode={Mode}, " +
                $"cadence={minHold}-{maxHold}{(minHold == maxHold ? " (locked)" : " (adaptive)")}");
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (Profile != null && !Profile.Global.Enabled) return;

            float t = Time.time - _startTime;
            float liveTau = ResolveTau();
            int liveCandidates = ResolveCandidates();
            int liveMinHold = ResolveMinHoldFrames();
            int liveMaxHold = ResolveMaxHoldFrames(liveMinHold);
            var overrides = ResolveBoneOverrides();

            // The null check here handles both AnySource mode (_stateWatcher is never
            // created) and AnimatorDriven mode where no Animator was found at Start.
            if (_stateWatcher != null && _stateWatcher.IsValid && _stateWatcher.Poll())
                FlushAllHolds();

            // Compute visibility once before the loop so each bone doesn't repeat the work.
            // When culled, schedulers still run (bones are read, state is updated) but the
            // localRotation write-back is skipped — no pop when the rig comes back on screen.
            bool culled = EnableVisibilityCulling && !IsVisible();

            // Held rotation of SmearReferenceBone for this frame — captured inside the loop
            // below rather than re-read from the transform afterward. Under visibility
            // culling bone.localRotation is never written back, so a post-loop read would
            // silently return last frame's stale value instead of what was just computed.
            Quaternion smearHeldRotation = _smearBoneIndex >= 0
                ? _bones[_smearBoneIndex].localRotation
                : Quaternion.identity;

            for (int i = 0; i < _bones.Length; i++)
            {
                Transform bone = _bones[i];
                if (bone == null || _excluded[i])
                {
                    if (bone != null)
                        _rawRotations[i] = bone.localRotation;
                    continue;
                }

                Quaternion raw = bone.localRotation;
                float response = ResolveResponseMultiplier(ComputeMotionIntensity(i, raw));
                float boneTau = ResolveTauForBone(bone, liveTau, overrides) * response;
                int boneCandidates = Mathf.Clamp(Mathf.RoundToInt(liveCandidates * response), 1, 4);

                // Sync tau / candidate density / cadence so live profile slider changes
                // take effect immediately.
                _schedulers[i].Tau = boneTau;
                _schedulers[i].CandidatesPerSegment = boneCandidates;
                _schedulers[i].MinHoldFrames = liveMinHold;
                _schedulers[i].MaxHoldFrames = liveMaxHold;

                Quaternion held = _schedulers[i].Update(t, raw);
                _rawRotations[i] = raw;
                if (i == _smearBoneIndex)
                    smearHeldRotation = held;
                if (!culled)
                    bone.localRotation = held;
            }

            if (_smearBoneIndex >= 0)
            {
                Quaternion rawRot  = _rawRotations[_smearBoneIndex];
                Quaternion heldRot = smearHeldRotation;

                // rawRot/heldRot are localRotations, which compose onto the PARENT's
                // frame, not SmearReferenceBone's own — TransformDirection must be called
                // on the parent, otherwise the result is rotated through the wrong frame.
                Transform parent = SmearReferenceBone.parent;
                Vector3 rawDir  = parent != null ? parent.TransformDirection(rawRot  * Vector3.forward) : rawRot  * Vector3.forward;
                Vector3 heldDir = parent != null ? parent.TransformDirection(heldRot * Vector3.forward) : heldRot * Vector3.forward;

                _rootSmearVector = rawDir - heldDir;

                _propertyBlock.SetVector("_SmearDirection", _rootSmearVector.normalized);
                _propertyBlock.SetFloat("_SmearStrength", _rootSmearVector.magnitude);
                foreach (var renderer in _renderers)
                    if (renderer is SkinnedMeshRenderer)
                        renderer.SetPropertyBlock(_propertyBlock);
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
                _rawRotations[i] = _bones[i].localRotation;
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

        private int ResolveBufferSize()
            => Profile != null ? Mathf.Max(4, Profile.LiveAnimation.BufferSize) : 30;

        private string[] ResolveExcludeKeywords()
            => Profile != null ? Profile.LiveAnimation.ExcludeKeywords : ExcludeKeywords;

        // Cadence bounds. HoldFrameScheduler defaults to 0 / int.MaxValue (pure Tau-gated
        // adaptive stepping); feeding real values here is what turns on the frame-level
        // cadence. When min == max, HoldFrameScheduler.Update()'s forceSnap branch is
        // evaluated before the Tau-gated allowSnap branch, so every bone snaps on exactly
        // the same frame — all counters start at 0 together in Start(), so the whole rig
        // stays in lockstep. That is the "animating on twos" case (min == max == 2).
        private int ResolveMinHoldFrames()
            => Mathf.Max(1, Profile != null ? Profile.LiveAnimation.MinHoldFrames : MinHoldFrames);

        // Clamped to >= min so an inverted profile setting degrades to a locked cadence
        // rather than forcing a snap every single frame (which would disable stepping).
        private int ResolveMaxHoldFrames(int minHoldFrames)
            => Mathf.Max(minHoldFrames,
                         Profile != null ? Profile.LiveAnimation.MaxHoldFrames : MaxHoldFrames);

        private OnTwosProfile.BoneOverride[] ResolveBoneOverrides()
            => Profile != null ? Profile.BoneOverrides : Array.Empty<OnTwosProfile.BoneOverride>();

        private float ResolveTauForBone(Transform bone, float baseTau, OnTwosProfile.BoneOverride[] overrides)
        {
            float overrideTau = BoneFilter.GetTauOverride(bone, overrides);
            return overrideTau > 0f ? overrideTau : baseTau;
        }

        private float ResolveResponseMultiplier(float motionIntensity)
        {
            if (Profile == null || Profile.Global == null || Profile.Global.ResponseCurve == null)
                return 1f;

            float multiplier = Profile.Global.ResponseCurve.Evaluate(Mathf.Clamp01(motionIntensity));
            return Mathf.Max(0.05f, multiplier);
        }

        private float ComputeMotionIntensity(int boneIndex, Quaternion currentRaw)
        {
            if (_rawRotations == null || boneIndex < 0 || boneIndex >= _rawRotations.Length)
                return 0f;

            Quaternion previousRaw = _rawRotations[boneIndex];
            if (previousRaw == default)
                return 0f;

            // 45 degrees maps to intensity 1; smaller changes keep the curve in the
            // lower end where ResponseCurve can soften stepping on slower motion.
            return Mathf.Clamp01(Quaternion.Angle(previousRaw, currentRaw) / 45f);
        }

        // ExcludeBones are Transform[] references — scene-object references that cannot
        // be stored in a profile asset. The profile only carries keyword-based exclusion.
        private Transform[] ResolveExcludeBones() => ExcludeBones;
    }
}