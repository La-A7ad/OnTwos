using OnTwos.Runtime.Utilities;
using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Optional wiring component. Drop this on any GameObject to connect an
    /// <see cref="OnTwosProfile"/>, <see cref="AnimationStepper"/>, and
    /// <see cref="RagdollStepper"/> in one place.
    ///
    /// Works on any rigged or physics-driven object — humanoid characters,
    /// creatures, props, vehicles, or anything else driven by an Animator
    /// or by Rigidbody simulation.
    ///
    /// On Awake (when <see cref="AutoBindOnAwake"/> is true) it adds and
    /// configures an AnimationStepper. To switch to physics-driven stepped
    /// motion call <see cref="ActivateRagdoll"/>. To reverse that call
    /// <see cref="Deactivate"/>.
    ///
    /// You don't have to use this component — both steppers work fine when
    /// added directly to a GameObject. This just wires the common case.
    /// </summary>
    [AddComponentMenu("CrunchyRagdoll/Authoring")]
    [DisallowMultipleComponent]
    public sealed class OnTwosAuthoring : MonoBehaviour, IOnTwosComponent
    {
        public OnTwosProfile Profile;
        public Animator      AnimatorRoot;
        public Transform     BoneRoot;

        [Tooltip("Root of the Rigidbody hierarchy to step. Works with any physics setup: " +
                 "joint ragdolls, single rigid bodies, compound colliders — anything " +
                 "that uses Rigidbody components. Auto-resolved on Awake if left empty.")]
        public Transform PhysicsRoot;

        [Tooltip("If true, AnimationStepper is added and configured on Awake. " +
                 "Disable to control timing manually.")]
        public bool AutoBindOnAwake = true;

        [Tooltip("If true, ActivateRagdoll() will add a RagdollStepper and build the " +
                 "visual proxy. If false, ActivateRagdoll() deactivates the " +
                 "AnimationStepper only — useful when you want to manage " +
                 "the RagdollStepper yourself.")]
        public bool AutoCreateProxy = true;

        [Tooltip("If true, also attach RagdollLogger when ActivateRagdoll() runs. " +
                 "Disable in shipping builds to suppress diagnostic output.")]
        public bool AddDiagnostics = false;

        [Tooltip("If true, every Rigidbody under the physics root is held kinematic while " +
                 "the rig is animator-driven, and released to dynamic by ActivateRagdoll(). " +
                 "Required for stability: the Ragdoll Wizard creates bodies non-kinematic, " +
                 "and AnimationStepper writes localRotation to those same bones every frame. " +
                 "A non-kinematic body driven by transform writes fights its CharacterJoint " +
                 "solver, accumulating corrective impulse until activation dumps it all at " +
                 "once. Disable only if your own code manages isKinematic.")]
        public bool ManageRigidbodyKinematics = true;

        [Tooltip("If true, linear and angular velocity are zeroed on every body at the moment " +
                 "the ragdoll is released. Gives a predictable limp collapse. Disable if you " +
                 "want to seed the bodies with your own momentum for a death impulse.")]
        public bool ZeroVelocityOnRelease = true;

        /// <summary>True after <see cref="ActivateRagdoll"/> has been called and before <see cref="Deactivate"/>.</summary>
        public bool IsRagdollActive => _isRagdollActive;

        private AnimationStepper _animStepper;
        private RagdollStepper   _ragdollStepper;
        private bool             _isRagdollActive;
        private Rigidbody[]      _physicsBodies;

        private void Awake()
        {
            AutoResolveBindings();

            // Cache and freeze before AttachAnimationStepper so the stepper never gets a
            // frame of writing bone rotations onto live, non-kinematic bodies.
            CachePhysicsBodies();
            if (ManageRigidbodyKinematics)
                SetBodiesKinematic(true);

            AttachAnimationStepper();
        }

        private void CachePhysicsBodies()
        {
            Transform root = PhysicsRoot != null ? PhysicsRoot : transform;
            _physicsBodies = root.GetComponentsInChildren<Rigidbody>(true);
        }

        /// <summary>
        /// Flip every tracked body between animator-driven (kinematic) and
        /// simulated (dynamic). Kinematic bodies ignore their CharacterJoints and
        /// accept transform writes without provoking the solver, which is exactly
        /// what AnimationStepper needs while it is holding stepped poses.
        /// </summary>
        private void SetBodiesKinematic(bool kinematic)
        {
            if (_physicsBodies == null) return;

            for (int i = 0; i < _physicsBodies.Length; i++)
            {
                Rigidbody rb = _physicsBodies[i];
                if (rb == null) continue;

                rb.isKinematic = kinematic;

                // Order matters: velocity writes to a kinematic body are discarded by
                // PhysX, so this has to happen after the body is already dynamic.
                // Otherwise the body inherits whatever motion it last held and launches
                // on activation instead of collapsing.
                if (!kinematic && ZeroVelocityOnRelease)
                {
#if UNITY_6000_0_OR_NEWER
                    rb.linearVelocity = Vector3.zero;
#else
                    rb.velocity = Vector3.zero;
#endif
                    rb.angularVelocity = Vector3.zero;
                }
            }
        }

        /// <summary>
        /// Resolve null binding slots using auto-binder heuristics. Public so the
        /// editor can call it via a "Try Auto-Bind" button. Warnings are editor-only;
        /// at runtime silent failure is preferred so objects that intentionally omit
        /// certain roots don't generate console noise.
        /// </summary>
        public void AutoResolveBindings()
        {
            if (AnimatorRoot == null)
                AnimatorRoot = OnTwosAutoBinder.FindAnimator(transform);
            if (BoneRoot == null)
                BoneRoot = OnTwosAutoBinder.FindBoneRoot(transform, AnimatorRoot);
            if (PhysicsRoot == null)
                PhysicsRoot = OnTwosAutoBinder.FindPhysicsRoot(transform);

#if UNITY_EDITOR
            if (AnimatorRoot == null)
                Debug.LogWarning($"[OnTwos] {gameObject.name}: Could not find an Animator. Assign AnimatorRoot manually.", this);
            if (BoneRoot == null)
                Debug.LogWarning($"[OnTwos] {gameObject.name}: Could not find a bone root. Assign BoneRoot manually.", this);
#endif
        }

        /// <summary>
        /// Idempotent: ensures an AnimationStepper exists on this object and is
        /// configured from the current profile / animator / bone-root values.
        /// </summary>
        public AnimationStepper AttachAnimationStepper()
        {
            if (_animStepper == null)
                _animStepper = GetComponent<AnimationStepper>() ?? gameObject.AddComponent<AnimationStepper>();

            _animStepper.Profile      = Profile;
            _animStepper.AnimatorRoot = AnimatorRoot;
            _animStepper.BoneRoot     = BoneRoot;
            _animStepper.enabled = true;
            return _animStepper;
        }

        /// <summary>
        /// Switch from animator-driven to physics-driven stepped motion.
        /// Deactivates the AnimationStepper (if present), then adds and configures
        /// a RagdollStepper. Safe to call multiple times — idempotent.
        ///
        /// Right-click the component header in Play Mode to test via the context menu.
        /// </summary>
        [ContextMenu("ActivateRagdoll (Test)")]
        public RagdollStepper ActivateRagdoll()
        {
            if (_isRagdollActive) return _ragdollStepper;
            _isRagdollActive = true;

            if (_animStepper != null)
                _animStepper.Deactivate();

            // The Animator has to stop before the bodies go dynamic, not after.
            // RagdollStepper disables it too, but only in its Start() — which lands at the
            // end of this frame. That would leave one full frame of the Animator driving
            // bone transforms while the bodies are already simulating, which is the exact
            // transform-vs-solver fight this handoff exists to avoid. Disabling here is
            // idempotent with RagdollStepper's own call.
            if (AnimatorRoot != null)
                AnimatorRoot.enabled = false;

            if (ManageRigidbodyKinematics)
                SetBodiesKinematic(false);

            if (!AutoCreateProxy) return null;

            _ragdollStepper              = GetComponent<RagdollStepper>() ?? gameObject.AddComponent<RagdollStepper>();
            _ragdollStepper.Profile      = Profile;
            _ragdollStepper.PhysicsRoot  = PhysicsRoot;

            if (AddDiagnostics && GetComponent<RagdollLogger>() == null)
                gameObject.AddComponent<RagdollLogger>();

            return _ragdollStepper;
        }

        /// <summary>
        /// Reverse a previous <see cref="ActivateRagdoll"/> call: destroys the
        /// RagdollStepper, restores source renderers, and re-attaches
        /// AnimationStepper so the object returns to animator-driven motion.
        /// </summary>
        public void Deactivate()
        {
            if (!_isRagdollActive) return;
            _isRagdollActive = false;

            if (_ragdollStepper != null)
            {
                Destroy(_ragdollStepper);
                _ragdollStepper = null;
            }

            // Undo everything ActivateRagdoll() and RagdollStepper.Start() changed.
            // Neither the stepper's OnDestroy nor its OnDisable restores the Animator, so
            // without this the rig stays limp: bodies dynamic, Animator off, and a freshly
            // re-attached AnimationStepper writing localRotation into live joints again.
            if (ManageRigidbodyKinematics)
                SetBodiesKinematic(true);

            if (AnimatorRoot != null)
                AnimatorRoot.enabled = true;

            AttachAnimationStepper();
        }

        /// <summary>
        /// Sanity check used by the inspector. Returns null when everything looks
        /// correct, or a plain-English description of the first issue found.
        /// </summary>
        public string Validate()
        {
            if (Profile == null)      return "No Profile assigned. Values fall back to per-component defaults.";
            if (AnimatorRoot == null) return "AnimatorRoot is null. Animator state transitions will not flush hold buffers.";
            if (BoneRoot == null)     return "BoneRoot is null. AnimationStepper will fall back to the root transform.";
            if (AutoCreateProxy && PhysicsRoot == null)
                return "PhysicsRoot is null and AutoCreateProxy is on. The proxy build will use the root GameObject.";
            if (AutoCreateProxy && !OnTwosAutoBinder.HasPhysicsBodies(PhysicsRoot ?? transform))
                return "PhysicsRoot has no Rigidbody components. The visual proxy will be empty.";
            return null;
        }

        private void OnValidate()
        {
            if (Profile == null) return;
            string issue = Validate();
            if (issue != null)
                Debug.LogWarning($"[OnTwos] {gameObject.name}: {issue}", this);
        }
    }
}