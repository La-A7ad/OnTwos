using OnTwos.Runtime.Utilities;
using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Drop this on the root of an enemy prefab to enable CrunchyRagdoll.
    ///
    /// On Awake (if AutoBindOnAwake is true) it ensures the rig has an
    /// AnimationStepper for live animation. When you want the ragdoll path to
    /// run, call <see cref="GoLimp"/> from your own death/damage code — that
    /// disables the AnimationStepper and adds a RagdollStepper that builds the
    /// visual proxy.
    ///
    /// You don't have to use this component. Both steppers work fine when
    /// added directly. This just wires the common case.
    /// </summary>
    [AddComponentMenu("CrunchyRagdoll/Authoring")]
    [DisallowMultipleComponent]
    public sealed class OnTwosAuthoring : MonoBehaviour, IOnTwosComponent
    {
        public OnTwosProfile Profile;
        public Animator AnimatorRoot;
        public Transform BoneRoot;
        public Transform RagdollRoot;

        [Tooltip("If true, AnimationStepper is added on Awake. Disable to control timing manually.")]
        public bool AutoBindOnAwake = true;

        [Tooltip("If true, GoLimp() will replace AnimationStepper with RagdollStepper " +
                 "and build the visual proxy. If false, GoLimp() does nothing — useful " +
                 "if you want only the live-animation half of the system.")]
        public bool AutoCreateProxy = true;

        [Tooltip("If true, also attach RagdollLogger when GoLimp() runs. Removes any logspam " +
                 "concerns by simply turning this off in shipping builds.")]
        public bool AddDiagnostics = false;

        private AnimationStepper _animStepper;
        private RagdollStepper _ragdollStepper;
        private bool _isLimp;

        private void Awake()
        {
            AutoResolveBindings();

            if (AutoBindOnAwake && Profile?.LiveAnimation != null)
                AttachAnimationStepper();
        }

        /// <summary>
        /// Resolve null binding slots using the auto-binder heuristics.
        /// Public so the editor can call it via a "Try Auto-Bind" button.
        /// </summary>
        public void AutoResolveBindings()
        {
            if (AnimatorRoot == null)
                AnimatorRoot = OnTwosAutoBinder.FindAnimator(transform);

            if (BoneRoot == null)
                BoneRoot = OnTwosAutoBinder.FindBoneRoot(transform, AnimatorRoot);

            if (RagdollRoot == null)
                RagdollRoot = OnTwosAutoBinder.FindRagdollRoot(transform);
        }

        /// <summary>
        /// Idempotent: ensures an AnimationStepper exists on this object and is
        /// wired to the configured profile/animator/bone-root.
        /// </summary>
        public AnimationStepper AttachAnimationStepper()
        {
            if (_animStepper == null)
                _animStepper = GetComponent<AnimationStepper>() ?? gameObject.AddComponent<AnimationStepper>();

            _animStepper.Profile = Profile;
            _animStepper.AnimatorRoot = AnimatorRoot;
            _animStepper.BoneRoot = BoneRoot;
            return _animStepper;
        }

        /// <summary>
        /// Transition from live-animation to ragdoll. Call this from your own
        /// death/hitstop code. Safe to call multiple times — it's idempotent.
        /// </summary>
        public RagdollStepper GoLimp()
        {
            if (_isLimp) return _ragdollStepper;
            _isLimp = true;

            if (_animStepper != null)
                _animStepper.Deactivate();

            if (!AutoCreateProxy) return null;

            _ragdollStepper = GetComponent<RagdollStepper>() ?? gameObject.AddComponent<RagdollStepper>();
            _ragdollStepper.Profile = Profile;
            _ragdollStepper.RagdollRoot = RagdollRoot;

            if (AddDiagnostics && GetComponent<RagdollLogger>() == null)
                gameObject.AddComponent<RagdollLogger>();

            return _ragdollStepper;
        }

        /// <summary>
        /// Quick sanity check used by the inspector. Returns null if everything
        /// looks correct, otherwise a description of the first issue found.
        /// </summary>
        public string Validate()
        {
            if (Profile == null) return "No Profile assigned. Live values will fall back to per-component defaults.";
            if (AnimatorRoot == null) return "AnimatorRoot is null. Animator state transitions will not flush hold buffers.";
            if (BoneRoot == null) return "BoneRoot is null. AnimationStepper will fall back to the root transform.";
            if (AutoCreateProxy && RagdollRoot == null) return "RagdollRoot is null and AutoCreateProxy is on. The proxy build will use the GameObject this component is on.";
            if (AutoCreateProxy && !OnTwosAutoBinder.HasRagdoll(RagdollRoot ?? transform))
                return "RagdollRoot has no CharacterJoint/HingeJoint/ConfigurableJoint. The visual proxy will be empty.";
            return null;
        }
    }
}
