using System;
using System.Collections.Generic;
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

        // Cached PCHIP fits — rebuilt only when _dirty is true.
        private Pchip _px, _py, _pz, _pw;
        private bool _dirty = true;

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

            // Invalidate cached fits and LUT so stale data can't be evaluated.
            _px = null;
            _py = null;
            _pz = null;
            _pw = null;
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
            return _px == null
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
            return _px == null
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
        /// </summary>
        public float[] ArcLengthCandidates(float a, float b, int n)
        {
            if (!_lutValid || n <= 0)
                return Array.Empty<float>();

            float angA = LutLerp(_lutTimes, _lutCumAngles, a);
            float angB = LutLerp(_lutTimes, _lutCumAngles, b);

            float totalAngle = angB - angA;
            if (totalAngle < 1e-5f)
            {
                // Near-flat segment — fall back to equal time (GL midpoint equivalent).
                float[] flat = new float[n];
                for (int i = 0; i < n; i++)
                    flat[i] = a + (b - a) * (i + 1f) / (n + 1f);
                return flat;
            }

            float[] result = new float[n];
            for (int i = 0; i < n; i++)
            {
                float targetAng = angA + totalAngle * (i + 1f) / (n + 1f);
                // Invert: angle → time. _lutCumAngles is monotone so swap roles.
                result[i] = LutLerp(_lutCumAngles, _lutTimes, targetAng);
            }
            return result;
        }

        // Rebuild PCHIP fits from current buffer contents, then build arc-length LUT.
        // Called at most once per Add() — all subsequent Evaluate/Derivative/
        // ArcLengthCandidates calls reuse cached data until the next sample arrives.
        private void RebuildIfDirty()
        {
            if (!_dirty) return;

            int n = Count;
            float[] xs = new float[n];
            Quaternion[] qs = new Quaternion[n];
            int start = Count < _capacity ? 0 : _head;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % _capacity;
                xs[i] = _times[idx];
                qs[i] = _rots[idx];
            }

            (xs, qs) = Deduplicate(xs, qs);
            if (xs.Length < 2) { _px = null; _lutValid = false; _dirty = false; return; }

            QuaternionSignNorm.Normalise(qs);

            int m = xs.Length;
            float[] qx = new float[m]; float[] qy = new float[m];
            float[] qz = new float[m]; float[] qw = new float[m];

            for (int i = 0; i < m; i++)
            {
                qx[i] = qs[i].x; qy[i] = qs[i].y;
                qz[i] = qs[i].z; qw[i] = qs[i].w;
            }

            _px = new Pchip(xs, qx);
            _py = new Pchip(xs, qy);
            _pz = new Pchip(xs, qz);
            _pw = new Pchip(xs, qw);

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

        private static (float[], Quaternion[]) Deduplicate(float[] xs, Quaternion[] qs)
        {
            List<float> outX = new List<float>(xs.Length);
            List<Quaternion> outQ = new List<Quaternion>(qs.Length);

            for (int i = 0; i < xs.Length; i++)
            {
                if (outX.Count > 0 && xs[i] <= outX[outX.Count - 1])
                    continue;

                outX.Add(xs[i]);
                outQ.Add(qs[i]);
            }

            return (outX.ToArray(), outQ.ToArray());
        }
    }
}