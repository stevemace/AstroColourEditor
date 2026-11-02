using System.Runtime.CompilerServices;
using AstroColour.Core.Colour;

namespace AstroColour.Core.Curves;

/// <summary>
/// A curve over the hue circle, as in Photoshop's or Resolve's hue-versus curves. X is the input hue as a fraction
/// of 360° in [0,1), wrapping round; Y is in [0,1], with 0.5 meaning no change. Points are interpolated with the same
/// monotone cubic spline as <see cref="ToneCurve"/>, continued periodically, so bumps never overshoot. With no
/// points the curve is flat at 0.5; with one it is flat at that point's value.
/// </summary>
public sealed class HueCurve
{
    /// <summary>The points a new or reset curve starts with: one at each primary and secondary hue.</summary>
    public const int DefaultPointCount = 6;

    public const float Neutral = 0.5f;

    public HueCurve() => Reset();

    public HueCurve(IEnumerable<CurvePoint> points) => Points.AddRange(points.OrderBy(p => p.X));

    /// <summary>Control points in increasing X order, each with X in [0,1).</summary>
    public List<CurvePoint> Points { get; } = [];

    public bool IsIdentity => Points.All(p => p.Y == Neutral);

    public HueCurve Clone() => new(Points);

    /// <summary>Back to a flat curve with a point at red, yellow, green, cyan, blue and magenta.</summary>
    public void Reset()
    {
        Points.Clear();
        for (int i = 0; i < DefaultPointCount; i++) Points.Add(new(i / (float)DefaultPointCount, Neutral));
    }

    public void CopyFrom(HueCurve other)
    {
        Points.Clear();
        Points.AddRange(other.Points);
    }

    public float Evaluate(float x) => Spline().Evaluate(Wrap(x));

    /// <summary>The curve evaluated at <paramref name="count"/> evenly spaced inputs from 0 to 1 inclusive.</summary>
    public float[] Sample(int count)
    {
        var spline = Spline();
        var values = new float[count];
        for (int i = 0; i < count; i++) values[i] = spline.Evaluate(i / (float)(count - 1));
        return values;
    }

    private ToneCurve.Spline Spline()
    {
        if (Points.Count == 0) return new ToneCurve.Spline([new(0f, Neutral), new(1f, Neutral)]);
        // Repeat the points a period either side so the spline wraps smoothly across 0°/360°.
        var extended = new List<CurvePoint>(Points.Count * 3);
        foreach (int shift in (int[])[-1, 0, 1])
            foreach (var p in Points)
                extended.Add(p with { X = p.X + shift });
        extended.Sort((a, b) => a.X.CompareTo(b.X));
        return new ToneCurve.Spline(extended);
    }

    private static float Wrap(float x) => x - MathF.Floor(x);
}

/// <summary>The hue-versus-hue and hue-versus-saturation curves.</summary>
public sealed class HueCurveSet
{
    /// <summary>The hue shift at the top or bottom of the hue-versus-hue graph, in degrees.</summary>
    public const float MaxHueShift = 90f;

    /// <summary>Hue versus hue: the hue shift for each input hue, ±<see cref="MaxHueShift"/>.</summary>
    public HueCurve Hue { get; private set; } = new();

    /// <summary>Hue versus saturation: 0 makes the hue grey, 0.5 leaves it, 1 doubles its saturation.</summary>
    public HueCurve Saturation { get; private set; } = new();

    public bool IsIdentity => Hue.IsIdentity && Saturation.IsIdentity;

    public HueCurveSet Clone() => new() { Hue = Hue.Clone(), Saturation = Saturation.Clone() };

    public void Reset()
    {
        Hue.Reset();
        Saturation.Reset();
    }

    public void CopyFrom(HueCurveSet other)
    {
        Hue.CopyFrom(other.Hue);
        Saturation.CopyFrom(other.Saturation);
    }

    /// <summary>The hue shift, in degrees, that a curve value gives.</summary>
    public static float HueShiftOf(float y) => (y - HueCurve.Neutral) * 2f * MaxHueShift;

    /// <summary>The saturation change, as a percentage (−100 to +100), that a curve value gives.</summary>
    public static float SaturationChangeOf(float y) => (y - HueCurve.Neutral) * 200f;

    /// <summary>
    /// A histogram of the image's hues, with each pixel counted by its chroma so the near-grey background, whose hue
    /// means little, hardly shows. Bin heights are square-root scaled to [0,1].
    /// </summary>
    /// <param name="baseAdjustments">If given, the histogram is of the image after these.</param>
    public static float[] Histogram(RgbImage image, int bins = 180, BaseAdjustments? baseAdjustments = null)
    {
        var counts = new double[bins];
        int step = Math.Max(1, image.PixelCount / 250_000);
        for (int i = 0; i < image.PixelCount; i += step)
        {
            float r = image.R[i], g = image.G[i], b = image.B[i];
            baseAdjustments?.Apply(ref r, ref g, ref b);
            ColourMath.RgbToHsl(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f),
                out float h, out _, out _, out float chroma);
            if (chroma <= 1e-7f) continue;
            counts[Math.Clamp((int)(h / 360f * bins), 0, bins - 1)] += chroma;
        }
        double max = counts.Max();
        if (max <= 0) max = 1;
        return counts.Select(n => (float)Math.Sqrt(n / max)).ToArray();
    }
}

/// <summary>A <see cref="HueCurveSet"/> tabulated for fast per-pixel evaluation.</summary>
public sealed class HueCurveLookup
{
    private const int Bins = 720; // half-degree steps, interpolated

    private readonly float[]? _hueShift;    // degrees
    private readonly float[]? _saturation;  // multiplier

    public HueCurveLookup(HueCurveSet curves)
    {
        if (!curves.Hue.IsIdentity)
            _hueShift = curves.Hue.Sample(Bins + 1).Select(HueCurveSet.HueShiftOf).ToArray();
        if (!curves.Saturation.IsIdentity)
            _saturation = curves.Saturation.Sample(Bins + 1).Select(y => 1f + HueCurveSet.SaturationChangeOf(y) / 100f).ToArray();
    }

    public bool IsEmpty => _hueShift is null && _saturation is null;

    /// <summary>
    /// Shifts the pixel's hue and scales its HSL saturation by the curves' values at its original hue, keeping its
    /// HSL lightness. Near-grey pixels change little, since their hue barely affects their colour.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Apply(ref float r, ref float g, ref float b)
    {
        if (IsEmpty) return;
        ColourMath.RgbToHsl(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f),
            out float h, out float s, out float l, out float chroma);
        if (chroma <= 1e-7f) return;

        float pos = h * (Bins / 360f);
        int i = (int)pos;
        if ((uint)i >= Bins) i = Bins - 1;
        float frac = pos - i;
        if (_hueShift is not null) h += _hueShift[i] + (_hueShift[i + 1] - _hueShift[i]) * frac;
        if (_saturation is not null) s = Math.Clamp(s * (_saturation[i] + (_saturation[i + 1] - _saturation[i]) * frac), 0f, 1f);
        ColourMath.HslToRgb(h, s, l, out r, out g, out b);
    }
}
