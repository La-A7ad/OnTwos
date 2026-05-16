using System;
using System.Collections.Generic;
using UnityEngine;

namespace OnTwos.Runtime.Math
{
    /// <summary>
    /// Full pipeline: PCHIP curve → extrema → arc-length hold candidates
    /// → deviation threshold → final hold frame sequence.
    ///
    /// One instance per bone. Each Update tick:
    ///   1. Feed new sample into MonotoneCubicSampler.
    ///   2. Find extrema over the current window via ExtremaDetector (throttled).
    ///   3. Within each monotone segment, place n candidates at equal rotation-angle
    ///      intervals via MonotoneCubicSampler.ArcLengthCandidates().
    ///   4. Walk candidates through the deviation threshold.
    ///   5. Return the current held pose.
    ///
    /// MinHoldFrames / MaxHoldFrames:
    ///   MinHoldFrames prevents snapping more often than once per N frames (jitter
    ///   guard on fast motion). MaxHoldFrames forces a snap after N frames even when
    ///   deviation hasn't crossed Tau (prevents frozen pose on slow / idle motion).
    ///   Both are frame-level counters, not candidate-level — they operate on the
    ///   whole Update() call, not on individual candidates within one walk.
    /// </summary>
    public sealed class HoldFrameScheduler
    {
        private readonly MonotoneCubicSampler _sampler;
        private Quaternion _held;
        private float _windowStart;

        // Writable so external callers can push live profile values before each
        // Update call. Not readonly — values can change in real time.
        public float Tau;

        /// <summary>
        /// Minimum Update() calls that must elapse between snaps.
        /// 0 = no minimum (default). Prevents sub-frame jitter on fast motion.
        /// </summary>
        public int MinHoldFrames = 0;

        /// <summary>
        /// Maximum Update() calls before a snap is forced regardless of deviation.
        /// int.MaxValue = never forced (default). Prevents frozen pose on slow motion.
        /// </summary>
        public int MaxHoldFrames = int.MaxValue;

        /// <summary>
        /// Arc-length candidates per monotone segment. Kept mutable so the profile's
        /// ResponseCurve can tune the density without rebuilding the scheduler.
        /// </summary>
        public int CandidatesPerSegment
        {
            get => _nCandidates;
            set => _nCandidates = value < 1 ? 1 : value > 4 ? 4 : value;
        }

        private int _nCandidates; // per monotone segment
        private int _framesSinceSnap;      // frames elapsed since the last snap

        // Extrema cache — recomputed every ExtremaInterval frames only.
        private List<float> _cachedExtrema = new List<float>();
        private int _framesSinceExtremaScan = 0;
        private const int ExtremaInterval = 10;

        /// <param name="tau">Degrees of rotation before a hold is emitted.</param>
        /// <param name="candidatesPerSegment">Arc-length candidates per monotone segment (1-4).</param>
        /// <param name="bufferSize">Rolling sample window size.</param>
        public HoldFrameScheduler(float tau = 15f, int candidatesPerSegment = 2, int bufferSize = 30)
        {
            if (candidatesPerSegment < 1 || candidatesPerSegment > 4)
                throw new ArgumentOutOfRangeException(nameof(candidatesPerSegment), "must be 1-4");

            _sampler = new MonotoneCubicSampler(bufferSize);
            Tau = tau;
            CandidatesPerSegment = candidatesPerSegment;
            _held = Quaternion.identity;
            _windowStart = -1f;
            _framesSinceSnap = 0;
        }

        /// <summary>
        /// Feed a new sample and return the current held pose.
        /// Call after reading the bone's animator-driven rotation.
        /// </summary>
        public Quaternion Update(float time, Quaternion boneRotation)
        {
            _sampler.Add(time, boneRotation);
            _framesSinceSnap++;

            // First sample after construction or Reset — seed the window and held pose.
            if (_windowStart < 0f)
            {
                _windowStart = time;
                _held = boneRotation;
                // Don't count the seed frame against MinHoldFrames.
                _framesSinceSnap = 0;
                return _held;
            }

            // Not enough history yet for a meaningful PCHIP fit — hold incoming pose.
            if (!_sampler.Ready)
            {
                _held = boneRotation;
                return _held;
            }

            float tStart = _windowStart;
            float tEnd   = time;

            if (tEnd - tStart < 1e-4f)
                return _held;

            // Recompute extrema only every ExtremaInterval frames.
            if (_framesSinceExtremaScan >= ExtremaInterval)
            {
                _cachedExtrema = ExtremaDetector.FindForBone(_sampler, tStart, tEnd);
                _framesSinceExtremaScan = 0;
            }
            _framesSinceExtremaScan++;

            // Build segment boundaries.
            // FIX (Bug 2): filter cached extrema to the current window (tStart, tEnd)
            // before building the boundaries list. Without this filter, extrema that
            // predate the current window start produce unsorted, out-of-range segment
            // boundaries that corrupt candidate placement.
            List<float> boundaries = new List<float>(_cachedExtrema.Count + 2) { tStart };
            foreach (float e in _cachedExtrema)
                if (e > tStart && e < tEnd)
                    boundaries.Add(e);
            if (boundaries[boundaries.Count - 1] < tEnd)
                boundaries.Add(tEnd);

            // Generate arc-length candidates within each monotone segment.
            List<float> candidates = new List<float>(boundaries.Count * _nCandidates);
            for (int seg = 0; seg < boundaries.Count - 1; seg++)
            {
                float a = boundaries[seg];
                float b = boundaries[seg + 1];
                if (b - a < 1e-5f) continue;
                float[] segCandidates = _sampler.ArcLengthCandidates(a, b, _nCandidates);
                candidates.AddRange(segCandidates);
            }
            candidates.Sort();

            // FIX (Bug C): enforce MinHoldFrames / MaxHoldFrames.
            bool allowSnap = _framesSinceSnap >= MinHoldFrames;
            bool forceSnap = MaxHoldFrames < int.MaxValue && _framesSinceSnap >= MaxHoldFrames;

            if (forceSnap)
            {
                // Force snap to the latest evaluated pose and reset the counter.
                _held = _sampler.Evaluate(tEnd);
                _framesSinceSnap = 0;
            }
            else if (allowSnap)
            {
                // Walk candidates through deviation threshold, chaining snaps across
                // the full window so the held pose reflects the latest step position.
                foreach (float t in candidates)
                {
                    if (t > time) break;
                    Quaternion evaluated = _sampler.Evaluate(t);
                    if (Quaternion.Angle(_held, evaluated) > Tau)
                    {
                        _held = evaluated;
                        _framesSinceSnap = 0;
                    }
                }
            }
            // else: MinHoldFrames not yet elapsed — return held without modification.

            // Advance window — drop oldest portion to keep buffer fresh.
            _windowStart = _sampler.OldestTime;

            return _held;
        }

        /// <summary>
        /// Reset scheduler to a new initial pose, clearing all sample history.
        ///
        /// FIX (Bug 1): previously only cleared _held / _windowStart / _cachedExtrema.
        /// The MonotoneCubicSampler buffer was left intact, causing pre-flush motion
        /// to bleed into the PCHIP fit for several frames after a state transition.
        /// Now calls _sampler.Clear() so the new state starts from a clean window.
        /// </summary>
        public void Reset(Quaternion initialPose)
        {
            _sampler.Clear();           // clear sample history — old frames cannot bleed in
            _held        = initialPose;
            _windowStart = -1f;
            _framesSinceSnap         = 0;
            _framesSinceExtremaScan  = ExtremaInterval; // force rescan next Update
            _cachedExtrema.Clear();
        }
    }
}