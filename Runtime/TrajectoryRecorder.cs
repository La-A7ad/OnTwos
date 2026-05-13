using System;
using UnityEngine;

namespace CrunchyRagdoll.Runtime.Recording
{
    /// <summary>
    /// Rolling fixed-timestep buffer for ragdoll trajectory capture.
    /// Each slot is preallocated and reused in a circular buffer — so once
    /// initialized, Capture() allocates zero garbage per FixedUpdate.
    /// </summary>
    public sealed class TrajectoryRecorder
    {
        private readonly RigidbodySnapshotFrame[] _frames;
        private int _head;

        public int Capacity => _frames.Length;
        public int Count { get; private set; }

        public TrajectoryRecorder(int capacity, int bodyCount)
        {
            if (capacity < 2)
                throw new ArgumentOutOfRangeException(nameof(capacity), "capacity must be at least 2");
            if (bodyCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(bodyCount), "bodyCount must be positive");

            _frames = new RigidbodySnapshotFrame[capacity];
            for (int i = 0; i < capacity; i++)
                _frames[i] = new RigidbodySnapshotFrame(bodyCount);
        }

        public RigidbodySnapshotFrame Capture(Rigidbody[] bodies, float time)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            if (bodies.Length != _frames[0].Count)
                throw new ArgumentException("Body array length does not match recorder layout.", nameof(bodies));

            var frame = _frames[_head];
            frame.CaptureFrom(bodies, time);

            _head = (_head + 1) % _frames.Length;
            if (Count < _frames.Length)
                Count++;

            return frame;
        }

        public RigidbodySnapshotFrame LatestFrame
        {
            get
            {
                if (Count == 0) return null;

                int latest = (_head - 1 + _frames.Length) % _frames.Length;
                return _frames[latest];
            }
        }
    }
}
