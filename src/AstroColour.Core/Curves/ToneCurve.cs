namespace AstroColour.Core.Curves;

/// <summary>A control point of a <see cref="ToneCurve"/>, with input and output in [0,1].</summary>
public readonly record struct CurvePoint(float X, float Y);

/// <summary>
/// A tone curve through a set of control points, interpolated with a monotone cubic (PCHIP)
/// spline so the curve never overshoots between points. Inputs left of the first point or right
/// of the last map to that point's output, which makes the end points act as black and white points.
/// </summary>
public sealed class ToneCurve
{
    public ToneCurve() => Points = [new(0f, 0f), new(1f, 1f)];

    public ToneCurve(IEnumerable<CurvePoint> points) => Points = [.. points.OrderBy(p => p.X)];

    /// <summary>Control points in increasing X order; there are always at least two.</summary>
    public List<CurvePoint> Points { get; }

    /// <summary>True if the curve maps every input to itself.</summary>
    public bool IsIdentity =>
        Points[0] == new CurvePoint(0f, 0f) && Points[^1] == new CurvePoint(1f, 1f) && Points.All(p => p.X == p.Y);

    public ToneCurve Clone() => new(Points);

    public void Reset()
    {
        Points.Clear();
        Points.Add(new(0f, 0f));
        Points.Add(new(1f, 1f));
    }

    public float Evaluate(float x) => new Spline(Points).Evaluate(x);

    /// <summary>The curve evaluated at <paramref name="count"/> evenly spaced inputs from 0 to 1.</summary>
    public float[] Sample(int count)
    {
        var spline = new Spline(Points);
        var values = new float[count];
        for (int i = 0; i < count; i++)
            values[i] = spline.Evaluate(i / (float)(count - 1));
        return values;
    }

    /// <summary>A cubic Hermite spline whose tangents are chosen to preserve monotonicity.</summary>
    internal sealed class Spline
    {
        private readonly float[] _x, _y, _m;

        public Spline(IReadOnlyList<CurvePoint> points)
        {
            int n = points.Count;
            _x = new float[n];
            _y = new float[n];
            _m = new float[n];
            for (int i = 0; i < n; i++) (_x[i], _y[i]) = (points[i].X, points[i].Y);
            if (n < 2) return;

            var h = new float[n - 1];
            var d = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                h[i] = _x[i + 1] - _x[i];
                d[i] = h[i] > 0f ? (_y[i + 1] - _y[i]) / h[i] : 0f;
            }

            _m[0] = d[0];
            _m[n - 1] = d[n - 2];
            for (int i = 1; i < n - 1; i++)
            {
                // Weighted harmonic mean of the neighbouring slopes, or flat at a local extremum.
                if (d[i - 1] * d[i] <= 0f) continue;
                float w1 = 2f * h[i] + h[i - 1], w2 = h[i] + 2f * h[i - 1];
                _m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
            }
        }

        public float Evaluate(float x)
        {
            int n = _x.Length;
            if (n == 0) return x;
            if (x <= _x[0]) return _y[0];
            if (x >= _x[n - 1]) return _y[n - 1];

            int k = Array.BinarySearch(_x, x);
            if (k >= 0) return _y[k];
            k = ~k - 1;

            float h = _x[k + 1] - _x[k];
            if (h <= 0f) return _y[k + 1];
            float t = (x - _x[k]) / h, t2 = t * t, t3 = t2 * t;
            float y = (2f * t3 - 3f * t2 + 1f) * _y[k] + (t3 - 2f * t2 + t) * h * _m[k]
                    + (-2f * t3 + 3f * t2) * _y[k + 1] + (t3 - t2) * h * _m[k + 1];
            return Math.Clamp(y, 0f, 1f);
        }
    }
}
