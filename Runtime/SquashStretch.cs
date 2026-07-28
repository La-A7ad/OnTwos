using OnTwos.Runtime.Utilities;
using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Procedural squash-and-stretch driven by the stepper's held-vs-raw divergence.
    ///
    /// While a pose is held, the bone the viewer sees stops moving but the real pose keeps
    /// going. That gap is the same residual that decides when to snap, and it is exactly
    /// what a smear needs: a direction (where the motion went) and a magnitude (how far
    /// behind the drawing has fallen). Feeding it into bone scale stretches the mesh along
    /// its own motion, so the smear is guaranteed to be in phase with the holds instead of
    /// being an independent effect that happens to run alongside them.
    ///
    /// Why bone scale rather than vertex displacement in a shader:
    ///
    ///   - Linear blend skinning already multiplies every vertex by its bones' matrices,
    ///     and those matrices carry scale. Writing localScale costs no extra draw call, no
    ///     extra skinning pass, and no shader — the GPU is reading that matrix regardless.
    ///
    ///   - Skinning blends weighted bones, so a vertex influenced 60/40 by two bones gets
    ///     a blend of both deformations. There are no seams at joints. A per-vertex shader
    ///     approach has to solve that explicitly, and assigning each vertex a single
    ///     dominant bone cannot solve it at all — two vertices either side of a joint get
    ///     different displacements and tear apart.
    ///
    /// The tradeoff is that Transform.localScale can only scale along the bone's own local
    /// axes, while a true stretch along an arbitrary axis d is I + (s-1)·(d dᵀ) — symmetric
    /// but not diagonal. See ComputeStretch for how close the diagonal gets and what is lost.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT [RequireComponent(typeof(AnimationStepper))]. OnTwosAuthoring adds
    /// the stepper itself at runtime, so requiring it would force an unconfigured
    /// AnimationStepper onto the prefab at author time and break the "authoring + profile
    /// is all you place" workflow. This resolves the stepper at runtime instead, whether it
    /// was placed by hand or added by authoring.
    /// </remarks>
    [AddComponentMenu("CrunchyRagdoll/Squash and Stretch")]
    [DefaultExecutionOrder(100)]   // after AnimationStepper writes its held rotations
    public sealed class SquashStretch : MonoBehaviour, IOnTwosComponent
    {
        [Tooltip("Stretch produced per metre of bone-tip divergence.\n\n" +
                 "Divergence is small in absolute terms — a limb in moderate motion drifts " +
                 "only a centimetre or two from its held pose over one hold — so this needs " +
                 "to be large. At 0.013 m of divergence, a Gain of 30 gives a ~35% stretch; " +
                 "a Gain of 4 gives 3% and is invisible.\n\n" +
                 "Watch DebugPeakDivergence in Play mode and set Gain so that " +
                 "Gain x DebugPeakDivergence lands near the stretch you want.")]
        [Range(0f, 200f)]
        public float Gain = 30f;

        public enum StretchAxis
        {
            /// <summary>
            /// Elongate each bone along its own length. A swinging arm gets longer, which is
            /// what reads as a smear. Always cleanly directional, because a bone's length
            /// axis is usually already aligned with one of its local axes.
            /// </summary>
            AlongBone,

            /// <summary>
            /// Stretch toward the direction the bone is actually moving. More physically
            /// motivated, but limited by localScale being diagonal in bone-local axes — a
            /// limb's tip moves perpendicular to its length, so this tends to widen the limb
            /// rather than extend it, and softens toward uniform inflation for diagonal
            /// motion. Raise Directionality to bias it back toward a single axis.
            /// </summary>
            AlongMotion
        }

        [Tooltip("AlongBone: the limb gets longer. This is what reads as a smear, and is the " +
                 "recommended default.\n\n" +
                 "AlongMotion: stretch toward where the bone is heading. More correct in " +
                 "principle, but a limb's tip moves sideways relative to its own length, so " +
                 "it tends to make limbs fatter rather than longer.")]
        public StretchAxis Axis = StretchAxis.AlongBone;

        [Tooltip("AlongMotion only. How sharply stretch concentrates on the single axis " +
                 "closest to the motion. 1 spreads it across all three, which reads as " +
                 "inflation; higher values bias toward one axis and read as a streak.")]
        [Range(1f, 8f)]
        public float Directionality = 3f;

        [Tooltip("Hard ceiling on the stretch factor. 1 = no stretch at all. 2 = a bone may " +
                 "reach twice its length. Prevents a single fast frame from tearing the rig.")]
        [Range(1f, 4f)]
        public float MaxStretch = 1.6f;

        [Tooltip("Preserve volume by squashing the axes perpendicular to the motion. This is " +
                 "the classic squash-and-stretch rule — a stretched shape thins out rather " +
                 "than simply growing. Disable for pure elongation.")]
        public bool PreserveVolume = true;

        [Tooltip("Seconds for the stretch to catch up to the divergence signal. 0 tracks it " +
                 "exactly, which pops back to neutral on the frame a pose snaps. A small " +
                 "value (0.03-0.08) lets the stretch fall off across the snap instead.")]
        [Range(0f, 0.25f)]
        public float Smoothing = 0.05f;

        [Tooltip("Ignore divergence below this magnitude, in metres. Keeps idle and " +
                 "slow-walk noise from producing a permanent low-level wobble.\n\n" +
                 "Keep it well under DebugPeakDivergence — at 5 mm it was eating roughly a " +
                 "third of the signal from a limb in moderate motion.")]
        [Range(0f, 0.05f)]
        public float Deadzone = 0.002f;

        [Header("Diagnostics (read-only, Play mode)")]
        [Tooltip("Largest bone-tip divergence seen this frame, in metres. If this stays 0 the " +
                 "signal is dead and Gain will never matter. If it is non-zero but small, " +
                 "raise Gain.")]
        public float DebugPeakDivergence;

        [Tooltip("Largest stretch factor actually applied this frame. 1 = no deformation.")]
        public float DebugPeakStretch;

        [Tooltip("Name of the bone currently stretching the most. Confirms the signal is " +
                 "landing on the limb you expect rather than on the root.")]
        public string DebugPeakBone;

        private AnimationStepper _stepper;
        private Vector3[] _originalScale;
        private Vector3[] _current;      // per-bone accumulated stretch, 1 = neutral
        private int[]     _parentIndex;  // index into the bone array, or -1
        private bool      _ready;

        private void Awake()
        {
            // Best case: the stepper is already here (placed by hand) and we request the
            // signal before its Start() decides whether to build it. When OnTwosAuthoring
            // adds the stepper instead, it does so from its own Awake — which runs first,
            // since this component sits at execution order 100 and authoring is at 0.
            _stepper = GetComponent<AnimationStepper>();
            _stepper?.EnableDivergenceSignal();
        }

        private void Start()
        {
            // Resolve again in case the stepper appeared after our Awake. EnableDivergenceSignal
            // builds the arrays on demand, so arriving late is recoverable rather than
            // silently producing no signal.
            if (_stepper == null)
            {
                _stepper = GetComponent<AnimationStepper>();
                _stepper?.EnableDivergenceSignal();
            }

            if (_stepper == null)
            {
                Debug.LogWarning(
                    $"[CrunchyRagdoll/SquashStretch] {gameObject.name}: no AnimationStepper " +
                    "found. Add one, or an OnTwosAuthoring that creates it. Disabling.", this);
                enabled = false;
                return;
            }

            if (_stepper.Bones == null) { enabled = false; return; }

            Transform[] bones = _stepper.Bones;
            _originalScale = new Vector3[bones.Length];
            _current       = new Vector3[bones.Length];
            _parentIndex   = new int[bones.Length];

            // Map each bone to its parent's slot so inherited scale can be cancelled.
            // GetComponentsInChildren returns depth-first with parents before children, so
            // a linear scan backwards finds the parent without a dictionary.
            for (int i = 0; i < bones.Length; i++)
            {
                _originalScale[i] = bones[i] != null ? bones[i].localScale : Vector3.one;
                _current[i]       = Vector3.one;
                _parentIndex[i]   = -1;

                if (bones[i] == null) continue;
                Transform parent = bones[i].parent;
                for (int p = i - 1; p >= 0; p--)
                {
                    if (bones[p] == parent) { _parentIndex[i] = p; break; }
                }
            }

            _ready = true;
        }

        private void LateUpdate()
        {
            if (!_ready) return;

            Vector3[] divergence = _stepper.BoneDivergence;
            if (divergence == null) return;

            Transform[] bones    = _stepper.Bones;
            bool[]      excluded = _stepper.BoneExcluded;

            // Exponential catch-up, framerate-independent. Smoothing == 0 tracks exactly.
            float k = Smoothing <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / Smoothing);

            DebugPeakDivergence = 0f;
            DebugPeakStretch    = 1f;

            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null) continue;

                Vector3 target = Vector3.one;
                if (!excluded[i])
                {
                    target = ComputeStretch(i, bone, divergence[i]);

                    float mag = divergence[i].magnitude;
                    if (mag > DebugPeakDivergence)
                    {
                        DebugPeakDivergence = mag;
                        DebugPeakBone       = bone.name;
                    }
                    float peak = Mathf.Max(target.x, Mathf.Max(target.y, target.z));
                    if (peak > DebugPeakStretch) DebugPeakStretch = peak;
                }

                _current[i] = Vector3.Lerp(_current[i], target, k);

                // Cancel the parent's stretch so each bone's effective scale is its own.
                // Scale compounds down a hierarchy, so without this the hand would inherit
                // the stretch of the forearm, the upper arm and the chest on top of its own.
                // Dividing by the parent's factor telescopes the chain back to exactly
                // _current[i] at every bone.
                Vector3 inherited = _parentIndex[i] >= 0 ? _current[_parentIndex[i]] : Vector3.one;

                Vector3 s = _originalScale[i];
                bone.localScale = new Vector3(
                    s.x * _current[i].x / Mathf.Max(inherited.x, 1e-4f),
                    s.y * _current[i].y / Mathf.Max(inherited.y, 1e-4f),
                    s.z * _current[i].z / Mathf.Max(inherited.z, 1e-4f));
            }
        }

        /// <summary>
        /// Turn a world-space divergence vector into a local scale.
        ///
        /// A true stretch of factor s along unit axis d is the symmetric matrix
        /// I + (s-1)·(d dᵀ), which localScale cannot represent — it only offers
        /// diag(sx, sy, sz) along the bone's own axes. The best available diagonal is the
        /// diagonal of that outer product, which is the SQUARED components (dx², dy², dz²).
        ///
        /// Squared rather than absolute matters: dx² + dy² + dz² = 1, so the components
        /// form a partition and the total stretch is conserved however the direction sits
        /// relative to the axes. Using |d·axis| instead would let a diagonal direction
        /// inflate all three axes at once, giving a balloon rather than a streak.
        ///
        /// What is lost is the off-diagonal (shear) part of the true tensor. In practice
        /// the stretch is accurate when motion aligns with a bone axis and softens toward
        /// uniform inflation as it goes diagonal — never wrong, just less directional.
        /// </summary>
        private Vector3 ComputeStretch(int i, Transform bone, Vector3 divergenceWorld)
        {
            float magnitude = divergenceWorld.magnitude;
            if (magnitude <= Deadzone) return Vector3.one;

            float stretch = Mathf.Clamp(1f + Gain * (magnitude - Deadzone), 1f, MaxStretch);

            // Perpendicular axes thin out as the dominant one extends. 1/sqrt(s) on both
            // makes the product exactly 1 for axis-aligned motion, so volume is preserved.
            float squash = PreserveVolume ? 1f / Mathf.Sqrt(stretch) : 1f;

            // The axis to stretch along, in bone-local space.
            Vector3 axis;
            if (Axis == StretchAxis.AlongBone)
            {
                // The bone's own length direction. Rigs almost always author bones so that
                // one local axis runs down the bone, which is precisely the case localScale
                // can express exactly — so this stays directional no matter which way the
                // motion happens to point.
                Vector3[] tips = _stepper.BoneTipOffsets;
                if (tips == null) return Vector3.one;
                axis = tips[i];
            }
            else
            {
                axis = bone.InverseTransformDirection(divergenceWorld);
            }

            float len = axis.magnitude;
            if (len <= 1e-6f) return Vector3.one;
            axis /= len;

            // Weights are the diagonal of the outer product (axis ⊗ axis), i.e. the squared
            // components — they sum to 1, so total stretch is conserved however the axis
            // sits relative to the bone's local axes. Using |component| instead would let a
            // diagonal axis inflate all three at once and give a balloon, not a streak.
            Vector3 w = new Vector3(axis.x * axis.x, axis.y * axis.y, axis.z * axis.z);

            // Sharpen toward the dominant axis. Raising each weight to a power and
            // renormalising keeps them a partition while concentrating the stretch, which
            // is what turns "everything inflates a bit" into "one axis streaks".
            if (Axis == StretchAxis.AlongMotion && Directionality > 1f)
            {
                w = new Vector3(
                    Mathf.Pow(w.x, Directionality),
                    Mathf.Pow(w.y, Directionality),
                    Mathf.Pow(w.z, Directionality));
                float sum = w.x + w.y + w.z;
                if (sum <= 1e-6f) return Vector3.one;
                w /= sum;
            }

            return new Vector3(
                Mathf.Lerp(squash, stretch, w.x),
                Mathf.Lerp(squash, stretch, w.y),
                Mathf.Lerp(squash, stretch, w.z));
        }

        /// <summary>
        /// Return every bone to the scale it had before this component touched it.
        /// Without this the rig would be left mid-stretch whenever the component is
        /// disabled, and because localScale is serialised on a prefab instance that
        /// deformation would persist into edit mode.
        /// </summary>
        public void ResetScales()
        {
            // _stepper may already be destroyed when this runs during teardown — Unity
            // tears components down in unspecified order, and touching a destroyed
            // MonoBehaviour's properties throws rather than returning null.
            if (!_ready || _stepper == null) return;

            Transform[] bones = _stepper.Bones;
            if (bones == null) return;
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                bones[i].localScale = _originalScale[i];
                _current[i] = Vector3.one;
            }
        }

        private void OnDisable() => ResetScales();
    }
}
