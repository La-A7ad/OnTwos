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

        [Header("Visual offset (foot planting)")]
        [Tooltip("Transform sitting between this GameObject and the rig root. When assigned, " +
                 "the rig's WORLD position is held for the duration of each step while the " +
                 "GameObject (and its collider) keeps moving smoothly.\n\n" +
                 "This is what stops feet sliding. Holding a bone's rotation freezes the leg " +
                 "pose, but the character keeps translating, so a planted foot skates. Holding " +
                 "world position for the same interval makes the foot genuinely static.\n\n" +
                 "Leave null to disable. Use the 'Create Visual Offset Root' button on this " +
                 "component to set one up.")]
        public Transform VisualOffsetRoot;

        [Range(0f, 0.5f)]
        [Tooltip("Maximum metres the rendered rig may lag behind its true position before a " +
                 "resync is forced. This is a SAFETY BOUND, not the step trigger — position " +
                 "normally releases on the same beat as the rotation cadence.\n\n" +
                 "Note it also offsets the rig from its colliders, which stay put. In a shooter " +
                 "that means shots resolve against the collider, not the visible mesh, so keep " +
                 "this small — 0.05 is a good starting point.")]
        public float MaxVisualOffset = 0.05f;

        [Header("Smear (whole-mesh MVP)")]
        [Tooltip("Bone used to derive the whole-mesh smear vector pushed to the " +
                 "_SmearDirection/_SmearStrength shader properties. Assign a bone that " +
                 "actually carries motion — e.g. Hips — not the Armature root, which is " +
                 "usually stationary. Leave null to disable smear.")]
        public Transform SmearReferenceBone;

        [Header("Fallback settings (used when Profile is null)")]
        [Range(0.5f, 45f)] public float Tau = 5f;
        [Range(1, 4)] public int CandidatesPerSegment = 2;

        [Range(1f, 60f)]
        [Tooltip("Cadence, in new poses per second. 12 = the classic 'on twos'. " +
                 "Measured in time, so the look is identical at any framerate.")]
        public float StepRate = 12f;

        [Range(0f, 1f)]
        [Tooltip("0 = locked metronome, every bone on the same beat (recommended). " +
                 "Above 0, bones may snap early off Tau and desynchronise.")]
        public float CadenceJitter = 0f;

        [Range(30f, 1440f)]
        [Tooltip("Rotation speed, in degrees per second, that maps to ResponseCurve input 1.0. " +
                 "Only used when a Profile with a ResponseCurve is assigned.")]
        public float MaxDegreesPerSecond = 360f;

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

        // Visual offset state. _heldWorldPos is where the rig is currently being drawn;
        // the GameObject's real transform continues to move underneath it.
        private Vector3 _heldWorldPos;
        private float   _lastPositionSnapTime;
        private bool    _visualOffsetReady;

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
            float       maxHold        = ResolveMaxHoldSeconds();
            float       minHold        = ResolveMinHoldSeconds(maxHold);
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
                _schedulers[i].MinHoldSeconds = minHold;
                _schedulers[i].MaxHoldSeconds = maxHold;
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

            // Seed the visual offset from the rig's starting position. Validated here
            // rather than trusted, because an offset root that isn't actually an ancestor
            // of the bones would silently move nothing.
            if (VisualOffsetRoot != null)
            {
                if (searchRoot == VisualOffsetRoot || searchRoot.IsChildOf(VisualOffsetRoot))
                {
                    _heldWorldPos      = VisualOffsetRoot.position;
                    _visualOffsetReady = true;
                }
                else
                {
                    Debug.LogWarning(
                        $"[CrunchyRagdoll/AnimationStepper] {gameObject.name}: " +
                        "VisualOffsetRoot is not an ancestor of the bone root, so offsetting " +
                        "it would not move the rig. Visual offset disabled.");
                }
            }

            _startTime = Time.time;
            _lastPositionSnapTime = 0f;
            _ready     = true;

            int active = 0;
            foreach (var e in _excluded) if (!e) active++;
            Debug.Log(
                $"[CrunchyRagdoll/AnimationStepper] {gameObject.name} — " +
                $"{active}/{_bones.Length} bones active, τ={tau}°, n={candidates}, mode={Mode}, " +
                $"cadence={1f / maxHold:F1} poses/sec" +
                $"{(Mathf.Approximately(minHold, maxHold) ? " (locked)" : $" (jitter to {1f / Mathf.Max(minHold, 1e-4f):F1})")}");
        }

        private void LateUpdate()
        {
            if (!_ready) return;
            if (Profile != null && !Profile.Global.Enabled) return;

            float t = Time.time - _startTime;
            float liveTau = ResolveTau();
            int liveCandidates = ResolveCandidates();
            float liveMaxHold = ResolveMaxHoldSeconds();
            float liveMinHold = ResolveMinHoldSeconds(liveMaxHold);
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
                _schedulers[i].MinHoldSeconds = liveMinHold;
                _schedulers[i].MaxHoldSeconds = liveMaxHold;

                Quaternion held = _schedulers[i].Update(t, raw);
                _rawRotations[i] = raw;
                if (i == _smearBoneIndex)
                    smearHeldRotation = held;
                if (!culled)
                    bone.localRotation = held;
            }

            UpdateVisualOffset(t, liveMaxHold);

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

            // Re-phase the position hold with the schedulers. Leaving it running would
            // put position and rotation on opposite halves of the beat after a state
            // transition, which reads as the rig creeping between steps.
            FlushVisualOffset();
            _lastPositionSnapTime = Time.time - _startTime;
        }

        public void Deactivate()
        {
            enabled = false;
        }

        // -----------------------------------------------------------------
        // Visual offset
        // -----------------------------------------------------------------

        /// <summary>
        /// Holds the rig's world position for the duration of each step, so a planted
        /// foot stays planted while the CharacterController keeps advancing smoothly.
        ///
        /// Runs on the same clock and the same interval as the bone schedulers, using
        /// the <paramref name="stepInterval"/> they were just given. That shared timebase
        /// is why this lives inside AnimationStepper rather than in its own component —
        /// a separate MonoBehaviour would keep its own clock and drift out of phase with
        /// the rotation cadence, so position and rotation would release on different
        /// frames and the foot would still creep.
        /// </summary>
        private void UpdateVisualOffset(float t, float stepInterval)
        {
            if (!_visualOffsetReady || VisualOffsetRoot == null) return;

            Vector3 truePos = transform.position;

            // Release on the beat, or early if the rig has drifted too far from where it
            // actually is. The distance check is a safety bound only: driving position
            // from distance alone cannot produce a stable cadence, because the threshold's
            // meaning depends on how fast the character happens to be moving.
            bool onBeat    = t - _lastPositionSnapTime >= stepInterval;
            bool tooFar    = MaxVisualOffset <= 0f ||
                             (truePos - _heldWorldPos).sqrMagnitude > MaxVisualOffset * MaxVisualOffset;

            if (onBeat || tooFar)
            {
                _heldWorldPos = truePos;

                if (onBeat && stepInterval > 1e-6f)
                {
                    // Advance on the fixed grid so the beat can't drift, matching
                    // HoldFrameScheduler's forceSnap bookkeeping.
                    _lastPositionSnapTime += Mathf.Floor((t - _lastPositionSnapTime) / stepInterval) * stepInterval;
                    if (t - _lastPositionSnapTime >= stepInterval) _lastPositionSnapTime = t;
                }
                else if (tooFar)
                {
                    _lastPositionSnapTime = t;
                }
            }

            VisualOffsetRoot.position = _heldWorldPos;
        }

        /// <summary>
        /// Re-seed the visual offset to the rig's true position. Call after teleporting
        /// the character, otherwise the rig visibly slides from the old location to the
        /// new one over the next step instead of arriving with it.
        /// </summary>
        public void FlushVisualOffset()
        {
            if (!_visualOffsetReady || VisualOffsetRoot == null) return;
            _heldWorldPos = transform.position;
            VisualOffsetRoot.position = _heldWorldPos;
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

        // Cadence bounds, in seconds. HoldFrameScheduler defaults to 0 / +Infinity
        // (pure Tau-gated adaptive stepping); feeding real values here is what turns
        // the cadence on. MaxHoldSeconds is the step interval; MinHoldSeconds opens a
        // window below it in which Tau may snap early. With jitter 0 the two are equal,
        // forceSnap always wins, and every bone shares one beat.
        private float ResolveMaxHoldSeconds()
        {
            float rate = Profile != null ? Profile.LiveAnimation.StepRate : StepRate;
            return 1f / Mathf.Max(0.01f, rate);
        }

        private float ResolveMinHoldSeconds(float maxHoldSeconds)
        {
            float jitter = Profile != null ? Profile.LiveAnimation.CadenceJitter : CadenceJitter;
            return maxHoldSeconds * (1f - Mathf.Clamp01(jitter));
        }

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

            // Degrees per SECOND, not per frame. Dividing raw per-frame delta by a
            // constant made this framerate-dependent: the same motion read ~2x higher
            // at 30fps than at 60, so ResponseCurve returned a different multiplier and
            // a bake (clip rate) could never match runtime (render rate).
            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            float degreesPerSecond = Quaternion.Angle(previousRaw, currentRaw) / dt;
            return Mathf.Clamp01(degreesPerSecond / Mathf.Max(1f, MaxDegreesPerSecond));
        }

        // ExcludeBones are Transform[] references — scene-object references that cannot
        // be stored in a profile asset. The profile only carries keyword-based exclusion.
        private Transform[] ResolveExcludeBones() => ExcludeBones;
    }
}