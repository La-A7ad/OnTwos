using System;
using UnityEngine;

namespace OnTwos.Runtime.Math
{
    /// <summary>
    /// Manages a rolling window of bone transform samples and provides
    /// PCHIP-smoothed evaluation at any queried time.
    ///
    /// One instance per bone. Maintains a fixed-size circular buffer of
    /// (time, localRotation) samples. PCHIP fits are cached and only rebuilt
    /// when a new sample arrives — not on every Evaluate/Derivative call.
    ///
    /// Also maintains an arc-length LUT (cumulative Quaternion.Angle vs time)
    /// rebuilt alongside the PCHIP cache. ArcLengthCandidates() uses this LUT
    /// to place n hold candidates at equal rotation-angle intervals within any
    /// sub-interval [a, b], via two linear interpolations — no per-query curve
    /// evaluation needed.
    /// </summary>
    public sealed class MonotoneCubicSampler
    {
        private readonly float[] _times;
        private readonly Quaternion[] _rots;
        private int _head;
        private readonly int _capacity;

        private const int MinSamples = 4;

        // Cached PCHIP fits — refitted in place, only when _dirty is true. Allocated
        // once in the constructor; RebuildIfDirty calls Fit() rather than constructing,
        // because every Add() dirties the cache and so this ran every frame per bone.
        private readonly Pchip _px, _py, _pz, _pw;
        private bool _dirty = true;
        private bool _fitValid;

        // Scratch buffers for RebuildIfDirty — the unrolled ring buffer, the
        // deduplicated sample set, and the four per-component channels handed to Pchip.
        // All sized to capacity and reused; the fit's own KnotCount says how much is live.
        private readonly float[] _scratchTimes;
        private readonly Quaternion[] _scratchRots;
        private readonly float[] _qx, _qy, _qz, _qw;

        // Arc-length LUT: parallel arrays, _lutTimes[i] ↔ _lutCumAngles[i].
        // _lutCumAngles is cumulative Quaternion.Angle from tMin, monotone increasing.
        // Built once per PCHIP rebuild. ArcLengthCandidates() interpolates into it.
        // Both arrays are allocated once in the constructor and reused on every rebuild
        // to avoid per-frame GC. _lutValid gates queries against partially-initialised
        // state (e.g. when there aren't yet enough samples for a meaningful fit).
        private readonly float[] _lutTimes;
        private readonly float[] _lutCumAngles;
        private bool _lutValid;
        private const int LutSize = 80; // 80 points across the window — ~1ms resolution at 60Hz

        public int Count { get; private set; }
        public bool Ready => Count >= MinSamples;

        public MonotoneCubicSampler(int capacity = 30)
        {
            if (capacity < MinSamples)
                throw new ArgumentException($"capacity must be >= {MinSamples}");

            _capacity = capacity;
            _times = new float[capacity];
            _rots = new Quaternion[capacity];

            // Pre-allocated once — reused on every BuildArcLengthLut to avoid GC.
            _lutTimes     = new float[LutSize];
            _lutCumAngles = new float[LutSize];
            _lutValid     = false;

            _scratchTimes = new float[capacity];
            _scratchRots  = new Quaternion[capacity];
            _qx = new float[capacity];
            _qy = new float[capacity];
            _qz = new float[capacity];
            _qw = new float[capacity];

            _px = new Pchip(capacity);
            _py = new Pchip(capacity);
            _pz = new Pchip(capacity);
            _pw = new Pchip(capacity);
            _fitValid = false;
        }

        /// <summary>
        /// Append a new sample. Marks cache dirty — next Evaluate/Derivative
        /// call will rebuild PCHIP fits once, then reuse until next Add().
        /// </summary>
        public void Add(float time, Quaternion rotation)
        {
            _times[_head] = time;
            _rots[_head] = rotation;
            _head = (_head + 1) % _capacity;
            if (Count < _capacity)
                Count++;

            _dirty = true;
        }

        /// <summary>
        /// Discard all samples and reset to the initial empty state.
        /// Called by HoldFrameScheduler.Reset() to prevent pre-flush motion
        /// from bleeding into the PCHIP fit after a state transition.
        /// </summary>
        public void Clear()
        {
            Count  = 0;
            _head  = 0;
            _dirty = true;

            // Invalidate cached fits and LUT so stale data can't be evaluated. The Pchip
            // objects survive — they are storage, and the flags are what gate reads.
            _fitValid = false;
            _lutValid = false;
        }

        /// <summary>
        /// Evaluate the PCHIP curve at time t.
        /// Returns raw latest sample if fewer than MinSamples collected.
        /// </summary>
        public Quaternion Evaluate(float t)
        {
            if (Count == 0) return Quaternion.identity;
            if (Count < MinSamples) return LatestRaw();

            RebuildIfDirty();
            return !_fitValid
                ? LatestRaw()
                : new Quaternion(
                    _px.Evaluate(t),
                    _py.Evaluate(t),
                    _pz.Evaluate(t),
                    _pw.Evaluate(t)).normalized;
        }

        /// <summary>
        /// First derivative of the PCHIP curve at time t, per quaternion component.
        /// Used by ExtremaDetector to find zero crossings.
        /// </summary>
        public Vector4 Derivative(float t)
        {
            if (Count < MinSamples) return Vector4.zero;

            RebuildIfDirty();
            return !_fitValid
                ? Vector4.zero
                : new Vector4(
                    _px.Derivative(t),
                    _py.Derivative(t),
                    _pz.Derivative(t),
                    _pw.Derivative(t));
        }

        /// <summary>
        /// Place <paramref name="n"/> hold candidates within [a, b] at equal
        /// rotation-angle intervals along the PCHIP curve.
        ///
        /// Uses the prebuilt arc-length LUT — no curve evaluation at query time.
        /// Two lerps per candidate: time→angle (to find angA/angB), angle→time
        /// (to find each target). Total cost: O(n · log(LutSize)).
        ///
        /// Results are written into <paramref name="dest"/> and the number written is
        /// returned. The caller owns the buffer so this can run once per monotone
        /// segment per bone per frame without allocating.
        /// </summary>
        public int ArcLengthCandidates(float a, float b, int n, float[] dest)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (!_lutValid || n <= 0) return 0;
            if (n > dest.Length)
                throw new ArgumentException($"dest holds {dest.Length}, needs {n}", nameof(dest));

            float angA = LutLerp(_lutTimes, _lutCumAngles, a);
            float angB = LutLerp(_lutTimes, _lutCumAngles, b);

            float totalAngle = angB - angA;
            if (totalAngle < 1e-5f)
            {
                // Near-flat segment — fall back to equal time (GL midpoint equivalent).
                for (int i = 0; i < n; i++)
                    dest[i] = a + (b - a) * (i + 1f) / (n + 1f);
                return n;
            }

            for (int i = 0; i < n; i++)
            {
                float targetAng = angA + totalAngle * (i + 1f) / (n + 1f);
                // Invert: angle → time. _lutCumAngles is monotone so swap roles.
                dest[i] = LutLerp(_lutCumAngles, _lutTimes, targetAng);
            }
            return n;
        }

        // Rebuild PCHIP fits from current buffer contents, then build arc-length LUT.
        // Called at most once per Add() — all subsequent Evaluate/Derivative/
        // ArcLengthCandidates calls reuse cached data until the next sample arrives.
        private void RebuildIfDirty()
        {
            if (!_dirty) return;

            // Unroll the ring buffer into the scratch arrays, dropping non-increasing
            // timestamps as we go. Deduplication is fused into the copy rather than
            // done as a separate pass so there is no intermediate array at all.
            int n = Count;
            int start = Count < _capacity ? 0 : _head;
            int m = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % _capacity;
                float t = _times[idx];
                if (m > 0 && t <= _scratchTimes[m - 1]) continue;

                _scratchTimes[m] = t;
                _scratchRots[m]  = _rots[idx];
                m++;
            }

            if (m < 2) { _fitValid = false; _lutValid = false; _dirty = false; return; }

            QuaternionSignNorm.Normalise(_scratchRots, m);

            for (int i = 0; i < m; i++)
            {
                Quaternion q = _scratchRots[i];
                _qx[i] = q.x; _qy[i] = q.y;
                _qz[i] = q.z; _qw[i] = q.w;
            }

            _px.Fit(_scratchTimes, _qx, m);
            _py.Fit(_scratchTimes, _qy, m);
            _pz.Fit(_scratchTimes, _qz, m);
            _pw.Fit(_scratchTimes, _qw, m);

            _fitValid = true;

            BuildArcLengthLut();

            _dirty = false;
        }

        // Build the arc-length LUT immediately after PCHIP fits are ready.
        // Samples the quaternion curve at LutSize evenly-spaced times and
        // accumulates Quaternion.Angle between consecutive samples.
        // Writes into the pre-allocated _lutTimes / _lutCumAngles arrays.
        private void BuildArcLengthLut()
        {
            float tMin = _px.TMin;
            float tMax = _px.TMax;
            float dt = (tMax - tMin) / (LutSize - 1);

            Quaternion prev = EvaluatePchip(tMin);
            _lutTimes[0] = tMin;
            _lutCumAngles[0] = 0f;

            for (int i = 1; i < LutSize; i++)
            {
                float t = tMin + i * dt;
                Quaternion curr = EvaluatePchip(t);
                _lutTimes[i] = t;
                _lutCumAngles[i] = _lutCumAngles[i - 1] + Quaternion.Angle(prev, curr);
                prev = curr;
            }

            _lutValid = true;
        }

        // Evaluate the four PCHIP components directly (no dirty check — only called
        // from RebuildIfDirty after fits are confirmed non-null).
        private Quaternion EvaluatePchip(float t) =>
            new Quaternion(
                _px.Evaluate(t), _py.Evaluate(t),
                _pz.Evaluate(t), _pw.Evaluate(t)).normalized;

        // Linear interpolation into a LUT. xs must be strictly increasing.
        // Clamps at both ends. Used for both time→angle and angle→time lookups.
        private static float LutLerp(float[] xs, float[] ys, float x)
        {
            if (x <= xs[0]) return ys[0];
            if (x >= xs[xs.Length - 1]) return ys[ys.Length - 1];

            int lo = 0, hi = xs.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (xs[mid] > x) hi = mid;
                else lo = mid;
            }

            float t = (x - xs[lo]) / (xs[hi] - xs[lo]);
            return ys[lo] + t * (ys[hi] - ys[lo]);
        }

        private Quaternion LatestRaw()
        {
            int idx = (_head - 1 + _capacity) % _capacity;
            return _rots[idx];
        }

        /// <summary>The timestamp of the oldest sample currently in the buffer.</summary>
        public float OldestTime
        {
            get
            {
                if (Count == 0) return 0f;
                int start = Count < _capacity ? 0 : _head;
                return _times[start];
            }
        }

    }
}