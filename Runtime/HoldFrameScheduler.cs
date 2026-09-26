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
    /// MinHoldSeconds / MaxHoldSeconds:
    ///   MinHoldSeconds prevents snapping more often than once per N seconds (jitter
    ///   guard on fast motion). MaxHoldSeconds forces a snap after N seconds even when
    ///   deviation hasn't crossed Tau (prevents frozen pose on slow / idle motion).
    ///   Both operate on the whole Update() call, not on individual candidates
    ///   within one walk.
    ///
    ///   These are deliberately measured in SECONDS, not Update() calls. Counting
    ///   ticks makes the cadence depend on whoever is driving the scheduler:
    ///   AnimationStepper ticks once per rendered frame (so "hold 2 ticks" means
    ///   72 poses/sec at 144fps but 15 at 30fps), RagdollStepper ticks on the fixed
    ///   50Hz physics clock, and the bake window ticks once per clip frame. All three
    ///   already pass a real timestamp into Update(), so gating on elapsed time makes
    ///   one StepRate mean the same thing everywhere — and makes a baked clip match
    ///   what Play mode previewed.
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
        /// Minimum seconds that must elapse between snaps.
        /// 0 = no minimum (default). Prevents sub-frame jitter on fast motion.
        /// </summary>
        public float MinHoldSeconds = 0f;

        /// <summary>
        /// Maximum seconds before a snap is forced regardless of deviation.
        /// PositiveInfinity = never forced (default). Prevents a frozen pose on slow motion.
        /// Set equal to MinHoldSeconds for an exact metronomic cadence.
        /// </summary>
        public float MaxHoldSeconds = float.PositiveInfinity;

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

        /// <summary>
        /// True when the most recent <see cref="Update"/> call emitted a new held pose,
        /// by either the forced-cadence or the Tau-crossing path. Valid until the next
        /// Update; a <see cref="Reset"/> clears it.
        ///
        /// Callers previously had to infer this by comparing the returned pose against
        /// the previous one with an angle epsilon, which cannot distinguish a real snap
        /// to a near-identical pose (an idle bone forced by MaxHoldSeconds) from no snap
        /// at all. The scheduler already knows; this just stops it throwing the fact away.
        /// Consumers: position coupling in RagdollStepper, and any smear technique that
        /// needs the snap instant rather than the residual.
        /// </summary>
        public bool DidSnap { get; private set; }

        // Whether Update has seeded the window yet. Replaces an earlier `_windowStart < 0`
        // sentinel, which silently assumed the caller's timebase never goes negative. All
        // three shipping drivers pass non-negative timestamps, but a caller that did not
        // would have re-seeded every tick and never stepped at all — a failure with no
        // symptom other than "it does nothing".
        private bool _seeded;

        // Timestamp of the last snap, in the caller's timebase. Stored as an absolute
        // time rather than an accumulated delta so repeated addition can't drift over
        // a long session. Negative means "no snap yet" (seeded on the first Update).
        private float _lastSnapTime;

        // Extrema cache — recomputed every ExtremaInterval frames only.
        private readonly List<float> _cachedExtrema = new List<float>(32);
        private int _framesSinceExtremaScan = 0;
        private const int ExtremaInterval = 10;

        // Per-Update working sets. Allocated once and Clear()ed each frame — List.Clear
        // keeps the backing array, so after the first few frames these stop growing
        // entirely. Previously both were `new`d every Update, on every bone, every frame,
        // which was the single largest steady-state allocation in the pipeline.
        private readonly List<float> _boundaries = new List<float>(32);
        private readonly List<float> _candidates = new List<float>(64);

        // Scratch for one segment's arc-length candidates. CandidatesPerSegment is
        // clamped to 4, so this is always large enough.
        private readonly float[] _segCandidates = new float[4];

        // Two candidates closer together than this are treated as the same moment and
        // merged. Matches ExtremaDetector.MinSegment (~1 frame at 60Hz), which collapses
        // extrema on the same grounds: below a frame they are numerically
        // indistinguishable, and a duplicate would only make the Tau walk compare the
        // held pose against itself.
        private const float CandidateMergeTolerance = 0.016f;

        /// <summary>
        /// True when the cadence bounds coincide, i.e. <c>CadenceJitter = 0</c> — the
        /// metronomic case. The Tau-gated candidate walk cannot run in this configuration
        /// because <c>forceSnap</c> is tested first and becomes true at the same instant
        /// <c>allowSnap</c> does, so the whole spline pipeline is dead weight here.
        ///
        /// Uses <c>&gt;=</c> rather than equality: a profile that resolves Min above Max
        /// (possible if StepRate and CadenceJitter are edited independently) is already
        /// behaving as locked, and should be treated as such rather than falling into the
        /// expensive path to compute a result it will discard.
        /// </summary>
        private bool Locked =>
            !float.IsPositiveInfinity(MaxHoldSeconds) && MinHoldSeconds >= MaxHoldSeconds;

        /// <summary>
        /// Restart the hold clock after a forced snap.
        ///
        /// Advances by whole step intervals rather than assigning <paramref name="time"/>,
        /// so the beat stays locked to a fixed grid instead of drifting forward by the frame
        /// overshoot every step. Guarded against a zero/denormal interval, and against a long
        /// stall (breakpoint, load hitch) producing a huge catch-up loop, by clamping to the
        /// current time.
        ///
        /// Shared by the locked and adaptive paths deliberately: the two must advance the
        /// grid identically or a baked clip stops matching what Play mode previewed.
        /// </summary>
        private void AdvanceSnapGrid(float time, float heldFor)
        {
            if (MaxHoldSeconds > 1e-6f)
            {
                _lastSnapTime += Mathf.Floor(heldFor / MaxHoldSeconds) * MaxHoldSeconds;
                if (time - _lastSnapTime >= MaxHoldSeconds) _lastSnapTime = time;
            }
            else _lastSnapTime = time;
        }

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
            _seeded = false;
            _windowStart = 0f;
            _lastSnapTime = 0f;
        }

        /// <summary>
        /// Feed a new sample and return the current held pose.
        /// Call after reading the bone's animator-driven rotation.
        /// </summary>
        public Quaternion Update(float time, Quaternion boneRotation)
        {
            // Cleared up front so every early return below reports "no snap". The seed
            // frame and the pre-Ready warm-up both assign _held directly, but neither is
            // a step — the pose is tracking continuously, which is the opposite of a hold.
            DidSnap = false;

            _sampler.Add(time, boneRotation);

            // First sample after construction or Reset — seed the window and held pose.
            if (!_seeded)
            {
                _seeded = true;
                _windowStart = time;
                _held = boneRotation;
                // Start the hold clock here so the seed frame isn't counted against
                // MinHoldSeconds. Every scheduler on a rig is seeded in the same
                // Start()/Reset() pass, which is what puts the whole rig in phase.
                _lastSnapTime = time;
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

            // Locked cadence bypasses the entire spline pipeline.
            //
            // forceSnap is tested before the Tau branch below, so when the two hold bounds
            // are equal the candidate walk is unreachable. Without this guard the extrema
            // scan, the segment boundaries and the arc-length candidates were computed and
            // then discarded, every bone, every tick, all to reach a forced snap that only
            // ever needed the newest sample.
            //
            // This path assigns boneRotation directly rather than evaluating the curve at
            // its newest knot. PCHIP interpolates its knots, so the two agree as rotations
            // (measured deviation 0.000000 deg), but only the direct copy is bit-identical
            // to the input: Evaluate renormalises, and QuaternionSignNorm may have negated
            // the newest sample before fitting. Bit-identity is a property of this branch,
            // not of the general forceSnap branch further down.
            //
            // Measured on a 13-bone humanoid at 50Hz: 40.4us -> 1.5us per rig per tick.
            //
            // Every branch above this point is deliberately left in the shared path, because
            // all of them affect *when* a snap lands: the seed frame, the Ready warm-up and
            // the degenerate-interval guard. Skipping any of them would shift the beat and
            // break parity with baked clips, which are produced by this same scheduler.
            if (Locked)
            {
                float lockedHeldFor = time - _lastSnapTime;
                if (lockedHeldFor >= MaxHoldSeconds)
                {
                    _held = boneRotation;
                    DidSnap = true;
                    AdvanceSnapGrid(time, lockedHeldFor);
                }

                // The extrema cache is not read on this path, so it is not maintained here
                // either — but CadenceJitter can be raised in Play mode, and the first
                // adaptive tick would then place candidates against whatever the cache held
                // before the locked run began. Arming the counter costs one assignment and
                // guarantees the transition starts from a fresh scan. It cannot affect
                // locked output, which never reads _cachedExtrema at all.
                _framesSinceExtremaScan = ExtremaInterval;

                _windowStart = _sampler.OldestTime;
                return _held;
            }

            // Bring the PCHIP fit and the arc-length LUT up to date for this tick before
            // anything reads them.
            //
            // ArcLengthCandidates performs no rebuild by design — it interpolates the LUT
            // arrays directly, which is what makes it allocation-free. Only Evaluate and
            // Derivative refit, and previously neither ran before candidate placement on
            // most ticks: the extrema scan is throttled to every tenth frame, and the Tau
            // walk's Evaluate calls come after. A bone still inside its MinHoldSeconds
            // window evaluated nothing at all, so one LUT could serve a whole extrema
            // interval. Measured candidate displacement from that staleness reached 22.4
            // degrees at a ten-frame gap — wider than a typical Tau, so it moved real
            // snap decisions rather than rounding them.
            //
            // tEnd is the newest knot, which is also exactly what the forced-snap branch
            // below assigns, so it is evaluated once here and reused rather than twice.
            // Nothing between this line and that branch adds a sample, so the value cannot
            // go stale within the tick.
            Quaternion newestEvaluated = _sampler.Evaluate(tEnd);

            // Recompute extrema only every ExtremaInterval frames.
            if (_framesSinceExtremaScan >= ExtremaInterval)
            {
                ExtremaDetector.FindForBone(_sampler, tStart, tEnd, _cachedExtrema);
                _framesSinceExtremaScan = 0;
            }
            _framesSinceExtremaScan++;

            // Build segment boundaries.
            // FIX (Bug 2): filter cached extrema to the current window (tStart, tEnd)
            // before building the boundaries list. Without this filter, extrema that
            // predate the current window start produce unsorted, out-of-range segment
            // boundaries that corrupt candidate placement.
            _boundaries.Clear();
            _boundaries.Add(tStart);
            for (int i = 0; i < _cachedExtrema.Count; i++)
            {
                float e = _cachedExtrema[i];
                if (e > tStart && e < tEnd)
                    _boundaries.Add(e);
            }
            if (_boundaries[_boundaries.Count - 1] < tEnd)
                _boundaries.Add(tEnd);

            // Generate arc-length candidates within each monotone segment.
            _candidates.Clear();
            for (int seg = 0; seg < _boundaries.Count - 1; seg++)
            {
                float a = _boundaries[seg];
                float b = _boundaries[seg + 1];
                if (b - a < 1e-5f) continue;

                int written = _sampler.ArcLengthCandidates(a, b, _nCandidates, _segCandidates);
                for (int i = 0; i < written; i++)
                    _candidates.Add(_segCandidates[i]);
            }

            // Interior segment boundaries are the detected motion extrema, and they are
            // eligible snap moments in their own right. ArcLengthCandidates places its
            // points strictly inside each segment — the (i+1)/(n+1) parameterisation never
            // reaches either end — so without this the walk could snap just before a
            // turning point or just after it, but never on it. The extremes are precisely
            // where an animator spends a drawing, so excluding them inverted the intent of
            // the whole arc-length scheme.
            //
            // Index 0 is tStart and the last entry is tEnd. Both are window edges, not
            // extrema, and must not be added: tEnd is the newest sample, already reachable
            // via forceSnap, and tStart is an artefact of where the buffer happens to begin.
            for (int i = 1; i < _boundaries.Count - 1; i++)
                _candidates.Add(_boundaries[i]);

            _candidates.Sort();

            // Merge near-duplicates in place. An extremum and an arc-length point from an
            // adjoining segment can land within the same frame. Compaction writes back over
            // the existing backing array and RemoveRange only moves elements, so the hot
            // path stays allocation-free.
            int keep = 0;
            for (int read = 0; read < _candidates.Count; read++)
            {
                if (keep > 0 &&
                    _candidates[read] - _candidates[keep - 1] < CandidateMergeTolerance)
                    continue;

                _candidates[keep++] = _candidates[read];
            }
            if (keep < _candidates.Count)
                _candidates.RemoveRange(keep, _candidates.Count - keep);

            // Cadence gate, in seconds of the caller's timebase.
            float heldFor   = time - _lastSnapTime;
            bool  allowSnap = heldFor >= MinHoldSeconds;
            bool  forceSnap = !float.IsPositiveInfinity(MaxHoldSeconds) && heldFor >= MaxHoldSeconds;

            // forceSnap is deliberately tested BEFORE the Tau-gated branch. When
            // MinHoldSeconds == MaxHoldSeconds (CadenceJitter = 0) this branch always
            // wins, Tau is bypassed for timing, and every bone snaps on exactly the
            // same beat — the metronomic "on twos" case.
            if (forceSnap)
            {
                // Force snap to the latest evaluated pose and restart the hold clock.
                _held = newestEvaluated;
                DidSnap = true;
                AdvanceSnapGrid(time, heldFor);
            }
            else if (allowSnap)
            {
                // Walk candidates through deviation threshold, chaining snaps across
                // the window so the held pose reflects the latest step position.
                //
                // The window reaches back as far as the oldest sample in the ring buffer —
                // roughly half a second — but the candidates inside it that PRECEDE the last
                // snap are already spent: the held pose was updated at _lastSnapTime, and
                // re-snapping to a moment before that means showing a pose the rig has
                // already moved on from. Skipping them fixes two things at once:
                //
                //   * the hold clock could run BACKWARDS. _lastSnapTime is stamped with the
                //     winning candidate's own time, so a qualifying candidate from the far
                //     end of the buffer moved it up to a full window into the past. The next
                //     tick then measured an inflated heldFor, tripped forceSnap, and emitted
                //     a step inside the MinHoldSeconds guard that exists to prevent exactly
                //     that. AdvanceSnapGrid repaired the grid on the same tick, so the
                //     cadence recovered — but the spurious step had already been drawn.
                //
                //   * on a reversing motion the newest poses sit within Tau of the running
                //     _held, so the LAST qualifying candidate could be an old one and the
                //     proxy would display a pose from up to half a second ago.
                for (int i = 0; i < _candidates.Count; i++)
                {
                    float t = _candidates[i];
                    if (t > time) break;
                    if (t <= _lastSnapTime) continue;
                    Quaternion evaluated = _sampler.Evaluate(t);
                    if (Quaternion.Angle(_held, evaluated) > Tau)
                    {
                        _held = evaluated;
                        DidSnap = true;
                        // Tau-driven snap: stamp the candidate's own time, not `time`.
                        // The snap conceptually happened at t, and using it keeps the
                        // next MinHoldSeconds window measured from the real event.
                        _lastSnapTime = t;
                    }
                }
            }
            // else: MinHoldSeconds not yet elapsed — return held without modification.

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
            _seeded      = false;
            _windowStart = 0f;
            // _lastSnapTime is re-seeded from the incoming timestamp on the next
            // Update() (the _windowStart < 0 branch), because Reset has no timebase
            // of its own. Resetting every scheduler together therefore re-phases the
            // whole rig onto one beat.
            _framesSinceExtremaScan  = ExtremaInterval; // force rescan next Update
            _cachedExtrema.Clear();
            DidSnap = false;
        }
    }
}