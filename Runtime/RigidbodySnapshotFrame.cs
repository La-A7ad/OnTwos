using System;
using UnityEngine;

namespace CrunchyRagdoll.Runtime.Recording
{
    /// <summary>
    /// A single fixed-timestep ragdoll frame containing a snapshot for each body.
    /// Preallocated and reused by <see cref="TrajectoryRecorder"/> to avoid
    /// per-step garbage.
    /// </summary>
    public sealed class RigidbodySnapshotFrame
    {
        private readonly RigidbodySnapshot[] _snapshots;

        public float Time { get; private set; }
        public int Count => _snapshots.Length;

        public RigidbodySnapshotFrame(int bodyCount)
        {
            if (bodyCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(bodyCount), "bodyCount must be positive");

            _snapshots = new RigidbodySnapshot[bodyCount];
        }

        public RigidbodySnapshot this[int index] => _snapshots[index];

        public void CaptureFrom(Rigidbody[] bodies, float time)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            if (bodies.Length != _snapshots.Length)
                throw new ArgumentException("Body array length does not match frame layout.", nameof(bodies));

            Time = time;
            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i];
                _snapshots[i] = rb != null ? new RigidbodySnapshot(rb) : default;
            }
        }

        public void CopyFrom(RigidbodySnapshotFrame other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));
            if (other._snapshots.Length != _snapshots.Length)
                throw new ArgumentException("Frame layouts do not match.", nameof(other));

            Time = other.Time;
            Array.Copy(other._snapshots, _snapshots, _snapshots.Length);
        }

        public RigidbodySnapshotFrame Clone()
        {
            var clone = new RigidbodySnapshotFrame(_snapshots.Length);
            clone.CopyFrom(this);
            return clone;
        }

        public void ApplyTo(Rigidbody[] bodies)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            if (bodies.Length != _snapshots.Length)
                throw new ArgumentException("Body array length does not match frame layout.", nameof(bodies));

            for (int i = 0; i < bodies.Length; i++)
            {
                Rigidbody rb = bodies[i];
                if (rb == null) continue;
                _snapshots[i].ApplyTo(rb);
            }
        }

        /// <summary>
        /// Drive a set of visual proxy transforms from this frame's snapshots.
        /// Used by the visual-proxy path where physics is left untouched and
        /// only transform state is mirrored.
        /// </summary>
        public void ApplyToTransforms(Transform[] bones)
        {
            int count = Mathf.Min(bones.Length, _snapshots.Length);

            for (int i = 0; i < count; i++)
            {
                var bone = bones[i];
                if (bone == null) continue;

                bone.position = _snapshots[i].Position;
                bone.rotation = _snapshots[i].Rotation;
            }
        }
    }
}
