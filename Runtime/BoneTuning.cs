using System;
using UnityEngine;

namespace OnTwos.Runtime
{
    /// <summary>
    /// Per-bone tuning addressed by a direct <see cref="Transform"/> reference rather
    /// than by name matching.
    ///
    /// This deliberately does NOT live on <see cref="OnTwosProfile"/>. A profile is a
    /// shared asset, and a ScriptableObject asset cannot hold a reference to a scene
    /// object — Unity nulls it on serialisation. Tuning that names specific bones is
    /// therefore per-rig data and belongs on the stepper component.
    ///
    /// Rig-agnostic by construction: drag the bone in and it works regardless of the
    /// naming convention the rig's author used, which is what keyword matching cannot
    /// promise. Keywords remain useful for bulk rules ("every bone with 'finger'");
    /// this is for the handful of bones that need individual attention.
    /// </summary>
    [Serializable]
    public class BoneTuning
    {
        [Tooltip("The bone this entry applies to. Drag it in from the rig hierarchy.")]
        public Transform Bone;

        [Tooltip("Exclude this bone from stepping entirely — it keeps its source motion, " +
                 "unstepped. Use for IK-corrected feet, which the stepper would otherwise " +
                 "freeze on held frames and undo the IK's per-frame correction.")]
        public bool Exclude;

        [Tooltip("Per-bone crunchiness threshold, in degrees. <= 0 means inherit the " +
                 "profile's Tau.")]
        public float TauOverride;

        [Tooltip("Per-bone remap of motion intensity (0..1) to a Tau multiplier. " +
                 "Leave empty to inherit the profile's global ResponseCurve.")]
        public AnimationCurve ResponseCurveOverride;

        /// <summary>
        /// True when a response curve was actually authored. An AnimationCurve with no
        /// keys evaluates to 0 for every input, which would silently drive Tau to zero
        /// and make the bone snap on every candidate — so an empty curve must be read
        /// as "not set" rather than passed through.
        /// </summary>
        public bool HasResponseCurve =>
            ResponseCurveOverride != null && ResponseCurveOverride.length > 0;
    }
}
