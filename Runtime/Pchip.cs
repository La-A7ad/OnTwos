using System;

namespace OnTwos.Runtime.Math
{
    /// <summary>
    /// Piecewise Cubic Hermite Interpolating Polynomial (scalar).
    /// Fritsch-Carlson (1980) monotonicity-preserving tangents.
    ///
    /// Properties:
    /// - No overshoot between consecutive data points (monotone-preserving)
    /// - No Runge's phenomenon (unlike Lagrange interpolation on uniform nodes)
    /// - C^1 continuous at knots (value + first derivative continuous)
    /// - Extrema sit at knots — between knots the cubic is monotone
    ///
    /// Compose four of these per quaternion (after sign normalisation) for rotations,
    /// or three per Vector3 for positions.
    ///
    /// Reference: Fritsch, F.N., Carlson, R.E. (1980).
    /// "Monotone Piecewise Cubic Interpolation".
    /// SIAM J. Numerical Analysis. 17 (2): 238-246.
    /// </summary>
    public sealed class Pchip
    {
        // Knot times (strictly increasing), values, and Fritsch-Carlson tangents.
        // Sized to the capacity handed to the constructor and reused across every
        // Fit() call — the live steppers refit four of these per bone per frame, so
        // allocating here would dominate the whole pipeline's GC cost.
        private float[] _x;
        private float[] _y;
        private float[] _m;
        // Secant slopes, scratch for ComputeTangents. Length _x.Length - 1 is enough.
        private float[] _d;
        // Number of knots currently fitted, which may be less than the array length.
        private int _n;

        /// <summary>
        /// Allocate an unfitted curve with room for <paramref name="capacity"/> knots.
        /// Call <see cref="Fit"/> before evaluating.
        /// </summary>
        public Pchip(int capacity)
        {
            if (capacity < 2) throw new ArgumentException("capacity must be >= 2");
            Allocate(capacity);
            _n = 0;
        }

        /// <summary>Construct from paired time/value samples.</summary>
        public Pchip(float[] x, float[] y)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));
            if (x.Length != y.Length)
                throw new ArgumentException($"x ({x.Length}) and y ({y.Length}) length mismatch");

            Allocate(x.Length);
            Fit(x, y, x.Length);
        }

        /// <summary>
        /// Refit this curve to the first <paramref name="count"/> entries of the given
        /// arrays, reusing the existing storage. The inputs are copied, so the caller is
        /// free to reuse its own scratch buffers immediately afterwards.
        /// </summary>
        public void Fit(float[] x, float[] y, int count)
        {
            if (x == null) throw new ArgumentNullException(nameof(x));
            if (y == null) throw new ArgumentNullException(nameof(y));
            if (count < 2)
                throw new ArgumentException("PCHIP requires at least 2 samples");
            if (count > x.Length || count > y.Length)
                throw new ArgumentException($"count ({count}) exceeds the supplied arrays");

            for (int i = 1; i < count; i++)
                if (x[i] <= x[i - 1])
                    throw new ArgumentException($"x must be strictly increasing; violation at index {i} (x[{i - 1}]={x[i - 1]}, x[{i}]={x[i]})");

            if (_x == null || _x.Length < count) Allocate(count);

            Array.Copy(x, _x, count);
            Array.Copy(y, _y, count);
            _n = count;

            ComputeTangents();
        }

        private void Allocate(int capacity)
        {
            _x = new float[capacity];
            _y = new float[capacity];
            _m = new float[capacity];
            _d = new float[capacity - 1];
        }

        /// <summary>False until <see cref="Fit"/> has run with at least two knots.</summary>
        public bool IsFitted => _n >= 2;

        public int KnotCount => _n;
        public float TMin => _x[0];
        public float TMax => _x[_n - 1];

        /// <summary>
        /// Evaluate curve at time t. Outside [TMin, TMax] the endpoint value is returned
        /// (constant extrapolation — animation clips don't extrapolate meaningfully).
        /// </summary>
        public float Evaluate(float t)
        {
            int n = _n;
            if (t <= _x[0]) return _y[0];
            if (t >= _x[n - 1]) return _y[n - 1];

            int i = FindSegment(t);
            float h = _x[i + 1] - _x[i];
            float s = (t - _x[i]) / h;
            float s2 = s * s;
            float s3 = s2 * s;

            // Cubic Hermite basis functions.
            float h00 = 2f * s3 - 3f * s2 + 1f;   // value at left knot
            float h10 = s3 - 2f * s2 + s;         // tangent at left knot (scaled by h)
            float h01 = -2f * s3 + 3f * s2;       // value at right knot
            float h11 = s3 - s2;                  // tangent at right knot (scaled by h)

            return h00 * _y[i]
                 + h10 * h * _m[i]
                 + h01 * _y[i + 1]
                 + h11 * h * _m[i + 1];
        }

        /// <summary>
        /// First derivative at time t. Needed by ExtremaDetector — extrema of the
        /// underlying motion are zero-crossings of this.
        /// </summary>
        public float Derivative(float t)
        {
            int n = _n;
            if (t <= _x[0] || t >= _x[n - 1]) return 0f; // flat extrapolation

            int i = FindSegment(t);
            float h = _x[i + 1] - _x[i];
            float s = (t - _x[i]) / h;
            float s2 = s * s;

            // Derivatives of Hermite basis w.r.t. t (= d/ds * 1/h).
            float dh00 = (6f * s2 - 6f * s) / h;
            float dh10 = (3f * s2 - 4f * s + 1f);
            float dh01 = (-6f * s2 + 6f * s) / h;
            float dh11 = (3f * s2 - 2f * s);

            return dh00 * _y[i]
                 + dh10 * _m[i]
                 + dh01 * _y[i + 1]
                 + dh11 * _m[i + 1];
        }

        /// <summary>Binary search for segment i where x[i] &lt;= t &lt; x[i+1].</summary>
        private int FindSegment(float t)
        {
            int lo = 0;
            int hi = _n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_x[mid] > t) hi = mid;
                else lo = mid;
            }
            return lo;
        }

        /// <summary>
        /// Compute Fritsch-Carlson tangents. Two-stage:
        /// (1) Initial tangents from weighted harmonic mean of secants (interior),
        ///     three-point formula at endpoints.
        /// (2) Clamp to satisfy the Fritsch-Carlson sufficient condition for
        ///     monotonicity within each segment.
        /// </summary>
        private void ComputeTangents()
        {
            int n = _n;
            float[] x = _x, y = _y, d = _d, m = _m;

            // Secant slopes d[i] = (y[i+1] - y[i]) / (x[i+1] - x[i]).
            for (int i = 0; i < n - 1; i++)
                d[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]);

            // --- Stage 1: initial tangents ---

            if (n == 2)
            {
                // Degenerate: a single segment is just a linear secant.
                m[0] = m[1] = d[0];
                return;
            }

            // Interior knots: weighted harmonic mean of adjacent secants.
            // Zero slope where secant signs differ (extremum sits at this knot).
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] * d[i] <= 0f)
                {
                    m[i] = 0f;
                }
                else
                {
                    float h0 = x[i] - x[i - 1];
                    float h1 = x[i + 1] - x[i];
                    float w0 = 2f * h1 + h0;
                    float w1 = h1 + 2f * h0;
                    // Weighted harmonic mean — provably monotone-preserving for interior knots.
                    m[i] = (w0 + w1) / (w0 / d[i - 1] + w1 / d[i]);
                }
            }

            // Endpoints: one-sided three-point estimate, then shape-preserving clamp.
            m[0] = EndpointTangent(d[0], d[1], x[1] - x[0], x[2] - x[1]);
            m[n - 1] = EndpointTangent(d[n - 2], d[n - 3], x[n - 1] - x[n - 2], x[n - 2] - x[n - 3]);

            // --- Stage 2: Fritsch-Carlson clamp (sufficient condition for monotonicity) ---
            // For each segment i, with α = m[i]/d[i], β = m[i+1]/d[i]:
            // if α² + β² > 9, scale tangents by τ = 3 / sqrt(α² + β²).
            for (int i = 0; i < n - 1; i++)
            {
                if (d[i] == 0f)
                {
                    // Flat segment — both endpoint tangents forced to zero, otherwise the
                    // cubic will overshoot or undershoot the constant value.
                    m[i] = 0f;
                    m[i + 1] = 0f;
                    continue;
                }

                float a = m[i] / d[i];
                float b = m[i + 1] / d[i];
                float r = a * a + b * b;

                if (r > 9f)
                {
                    float tau = 3f / (float)System.Math.Sqrt(r);
                    m[i] = tau * a * d[i];
                    m[i + 1] = tau * b * d[i];
                }
            }
        }

        /// <summary>
        /// One-sided three-point endpoint tangent with shape-preserving clamp.
        /// d0 = nearer secant, d1 = next-over secant.
        /// h0 = nearer spacing,  h1 = next-over spacing.
        /// Reference: de Boor, "A Practical Guide to Splines", endpoint conditions.
        /// </summary>
        private static float EndpointTangent(float d0, float d1, float h0, float h1)
        {
            float m = ((2f * h0 + h1) * d0 - h0 * d1) / (h0 + h1);

            // Clamp 1: opposite sign to nearer secant ⇒ force flat (extremum at endpoint).
            if (m * d0 <= 0f) return 0f;

            // Clamp 2: secants differ in sign and estimate exceeds 3·d0 ⇒ cap at 3·d0.
            if (d0 * d1 <= 0f && System.Math.Abs(m) > System.Math.Abs(3f * d0))
                return 3f * d0;

            return m;
        }
    }
}
