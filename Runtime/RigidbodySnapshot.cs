using UnityEngine;

namespace CrunchyRagdoll.Runtime.Recording
{
    /// <summary>
    /// Immutable per-body physics snapshot used by the ragdoll stepper.
    /// Captures the full Rigidbody state needed to restore a body atomically:
    /// position, rotation, linear velocity, and angular velocity.
    ///
    /// Restoring all four together (vs. writing only the transform) is what
    /// keeps CharacterJoint constraint solvers happy when the snapshot is
    /// played back into the physics rig — though in the visual-proxy path
    /// only Position and Rotation are read.
    /// </summary>
    public readonly struct RigidbodySnapshot
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Velocity;
        public readonly Vector3 AngularVelocity;

        public RigidbodySnapshot(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            Position = position;
            Rotation = rotation;
            Velocity = velocity;
            AngularVelocity = angularVelocity;
        }

        public RigidbodySnapshot(Rigidbody rb)
        {
            Position = rb.position;
            Rotation = rb.rotation;
#if UNITY_6000_0_OR_NEWER
            Velocity = rb.linearVelocity;
#else
            Velocity = rb.velocity;
#endif
            AngularVelocity = rb.angularVelocity;
        }

        public void ApplyTo(Rigidbody rb)
        {
            if (rb == null) return;

            // Restore the full dynamic state in one go. Required when feeding a
            // snapshot back into a non-kinematic body — otherwise CharacterJoint
            // constraint error accumulates and bodies drift over time.
            rb.position = Position;
            rb.rotation = Rotation;
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = Velocity;
#else
            rb.velocity = Velocity;
#endif
            rb.angularVelocity = AngularVelocity;
        }
    }
}
