using System;
using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Asset-driven configuration for the OnTwos stepped-animation system.
    ///
    /// Create one per stylistic preset (e.g. "Default Crunch", "Heavy Stop-Motion",
    /// "Subtle"), then assign to an OnTwosAuthoring component. Multiple rigs can
    /// share one profile; edits propagate to every user on save.
    ///
    /// All values are tuning data; nothing here is per-instance state. Per-instance
    /// state lives on AnimationStepper / RagdollStepper.
    /// </summary>
    [CreateAssetMenu(fileName = "OnTwosProfile",
                     menuName  = "CrunchyRagdoll/Profile",
                     order     = 100)]
    public sealed class OnTwosProfile : ScriptableObject
    {
        public GlobalSettings        Global        = new GlobalSettings();
        public LiveAnimationSettings LiveAnimation = new LiveAnimationSettings();
        public RagdollSettings       Ragdoll       = new RagdollSettings();
        public SettlingSettings      Settling      = new SettlingSettings();
        public ProxySettings         Proxy         = new ProxySettings();

        [Tooltip("Per-bone-name overrides that take precedence over the global ExcludeKeywords list.")]
        public BoneOverride[] BoneOverrides = Array.Empty<BoneOverride>();

        // -----------------------------------------------------------------
        // Nested settings blocks — one Inspector foldout per concept.
        // -----------------------------------------------------------------

        [Serializable]
        public class GlobalSettings
        {
            [Tooltip("Master switch. If false, neither stepper does anything.")]
            public bool Enabled = true;

            [Tooltip("Remap of normalised motion intensity (0..1) to a multiplier on Tau, " +
                     "hold duration, candidate count, and snap aggressiveness. " +
                     "Use to soften crunchiness on slow motions and sharpen it on fast ones.")]
            public AnimationCurve ResponseCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        }

        [Serializable]
        public class LiveAnimationSettings
        {

            [Range(1f, 60f)]
            [Tooltip("Cadence, in new poses per second. This is THE control for the stepped look.\n\n" +
                     "12 = the classic 'on twos' (24fps film held 2 frames). 8 = on threes. " +
                     "24+ = barely stepped.\n\n" +
                     "Measured in time, not engine frames, so the look is identical at 30, 60 " +
                     "or 144 fps — and a baked clip matches what Play mode previewed.")]
            public float StepRate = 12f;

            [Range(0f, 1f)]
            [Tooltip("How far bones may drift off the shared beat.\n\n" +
                     "0 = locked metronome: every bone snaps on exactly the same frame. This is " +
                     "the traditional look and the recommended default.\n\n" +
                     "Above 0, a bone whose motion crosses Tau early may snap ahead of the beat, " +
                     "so limbs desynchronise. 1 = fully Tau-driven, no shared beat at all. " +
                     "Useful deliberately — Spider-Punk's jacket running on its own rate from " +
                     "his body — but reads as stutter if you didn't mean it.")]
            public float CadenceJitter = 0f;

            [Range(0.5f, 45f)]
            [Tooltip("Crunchiness threshold for the live animation stepper, in degrees. " +
                     "5° = subtle stepping. 15° = obvious stop-motion. 30° = very chunky. " +
                     "Lower = more frames sampled = smoother. Higher = longer holds = chunkier.")]
            public float AnimTau = 5f;

            [Range(0f, 0.5f)]
            [Tooltip("BAKE-TIME ONLY. World-units (metres) of bone translation before a " +
                     "stepped position snaps in a baked clip. 0 = rotation-only (default). " +
                     "0.02 = subtle, 0.08 = obvious.\n\n" +
                     "Read by the Bake Clip window to write stepped localPosition curves. " +
                     "The runtime AnimationStepper does NOT read this — it steps rotation " +
                     "only.\n\n" +
                     "This does NOT fix foot sliding. Sliding comes from the character's " +
                     "WORLD position advancing while leg rotations are held, and a clip only " +
                     "drives bone-local transforms. Use AnimationStepper's VisualOffsetRoot " +
                     "for that. For runtime physics position stepping see Ragdoll > " +
                     "RagdollPosTau. See BAKING.md.")]
            public float PositionTau = 0f;

            [Range(1, 4)]
            [Tooltip("Arc-length hold candidates per monotone segment. 2 is a good default.")]
            public int GaussPoints = 2;

            [Tooltip("Rolling sample window per bone. ~30 covers half a second at 60 Hz.")]
            public int BufferSize = 30;

            [Tooltip("Bones whose names contain any of these substrings (case-insensitive) " +
                     "are excluded from stepping. Add entries specific to your rig — e.g. " +
                     "end-effector bones that clip geometry when held.")]
            public string[] ExcludeKeywords = Array.Empty<string>();
        }

        [Serializable]
        public class RagdollSettings
        {
            [Range(0.5f, 60f)]
            [Tooltip("Degrees of rotation before the visual proxy snaps to the live physics pose. " +
                     "12° = moderate crunch. 5° = subtle. 30° = very chunky stop-motion.")]
            public float RagdollTau = 12f;

            [Range(0.001f, 0.5f)]
            [Tooltip("World-space translation (metres) before the proxy snaps. " +
                     "0.05 = subtle. 0.15 = obvious. Scale with your rig's world-space size.")]
            public float RagdollPosTau = 0.08f;

            [Range(1f, 60f)]
            [Tooltip("Ragdoll cadence, in new poses per second. 12 = classic 'on twos'.\n\n" +
                     "Independent of the live-animation StepRate so a dying character can " +
                     "step at a different rate than a walking one.")]
            public float StepRate = 12f;

            [Range(0f, 1f)]
            [Tooltip("0 = every tracked body snaps on the same beat (recommended). " +
                     "Above 0, bodies whose motion crosses Tau early snap ahead of it. " +
                     "On a ragdoll a little jitter can read as loose, flailing weight.")]
            public float CadenceJitter = 0f;
        }

        [Serializable]
        public class SettlingSettings
        {
            [Tooltip("Linear speed (m/s) below which a body counts as still for settle detection.")]
            public float SettleVelocityThreshold = 0.75f;

            [Tooltip("Angular speed (deg/s) below which a body counts as still.")]
            public float SettleAngularThreshold = 25f;

            [Tooltip("How long all tracked bodies must stay below the still thresholds before " +
                     "the ragdoll is declared settled and the proxy locks at its current pose.")]
            public float SettleTime = 0.35f;

            [Tooltip("Linear speed (m/s) on the anchor body that wakes the proxy back up " +
                     "after settling — for example, when an external force disturbs the rig.")]
            public float WakeVelocityThreshold = 3.0f;
        }

        [Serializable]
        public class ProxySettings
        {
            [Tooltip("Hide the original Renderers on the source rig so only the proxy is visible. " +
                     "Disable to debug both layers simultaneously.")]
            public bool HideSourceRenderers = true;

            [Tooltip("Strip MonoBehaviours, Animators, and physics from the proxy clone. " +
                     "Required for the proxy to behave as a pure visual surface.")]
            public bool StripProxyComponents = true;

            [Tooltip("Force-enable all Renderers on the proxy after build. Some game-side cleanup " +
                     "scripts disable renderers during DestroyImmediate; this overrides that.")]
            public bool ForceEnableProxyRenderers = true;
        }

        [Serializable]
        public class BoneOverride
        {
            [Tooltip("Name substring matched case-insensitively against bone names.")]
            public string NameContains;

            [Tooltip("If true, this bone is force-excluded from stepping regardless of ExcludeKeywords.")]
            public bool ForceExclude;

            [Tooltip("Optional per-bone tau override. <= 0 means use the profile default.")]
            public float TauOverride;
        }

    }
}
