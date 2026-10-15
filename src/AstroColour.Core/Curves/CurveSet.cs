using System.Runtime.CompilerServices;
using AstroColour.Core.Colour;

namespace AstroColour.Core.Curves;

public enum CurveChannel
{
    /// <summary>The composite curve, applied to all three channels.</summary>
    Rgb,
    Red,
    Green,
    Blue,
}

/// <summary>Tone curves for the composite and each colour channel, in the style of Photoshop's Curves.</summary>
public sealed class CurveSet
{
    private readonly ToneCurve[] _curves = [new(), new(), new(), new()];

    public ToneCurve this[CurveChannel channel] => _curves[(int)channel];

    public bool IsIdentity => _curves.All(c => c.IsIdentity);

    public CurveSet Clone()
    {
        var copy = new CurveSet();
        for (int i = 0; i < _curves.Length; i++) copy._curves[i] = _curves[i].Clone();
        return copy;
    }

    public void Reset()
    {
        foreach (var curve in _curves) curve.Reset();
    }

    /// <summary>
    /// Sparse histograms of the image for drawing behind the curves, indexed by <see cref="CurveChannel"/>
    /// (luminance for <see cref="CurveChannel.Rgb"/>). Bin heights are square-root scaled to [0,1].
    /// </summary>
    /// <param name="selection">If given, each pixel counts by how strongly this colour range selects it.</param>
    /// <param name="narrowband">If given, the histogram is of the normalised image.</param>
    public static float[][] Histogram(RgbImage image, int bins = 256, ColourAdjustment? selection = null,
        NarrowbandNormalization? narrowband = null)
    {
        var counts = new double[4][];
        for (int c = 0; c < 4; c++) counts[c] = new double[bins];
        var weights = selection is null ? null : new AdjustmentSet([selection], includeAll: true);
        int step = Math.Max(1, image.PixelCount / (weights is null ? 1_000_000 : 250_000));
        for (int i = 0; i < image.PixelCount; i += step)
        {
            float r = image.R[i], g = image.G[i], b = image.B[i];
            narrowband?.Apply(ref r, ref g, ref b);
            float w = 1f;
            if (weights is not null)
            {
                ColourMath.RgbToHsl(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f),
                    out float h, out _, out float l, out float chroma);
                w = chroma <= 1e-7f ? 0f : weights.Weight(h, chroma, l);
                if (w <= 0f) continue;
            }
            counts[0][Bin(0.2126f * r + 0.7152f * g + 0.0722f * b, bins)] += w;
            counts[1][Bin(r, bins)] += w;
            counts[2][Bin(g, bins)] += w;
            counts[3][Bin(b, bins)] += w;
        }

        return counts.Select(channel =>
        {
            // Scale to the tallest interior bin, so clipped blacks or whites don't flatten the rest.
            double max = channel.Skip(1).Take(bins - 2).DefaultIfEmpty(0).Max();
            if (max <= 0) max = Math.Max(channel[0], channel[^1]);
            if (max <= 0) max = 1;
            return channel.Select(n => (float)Math.Min(1, Math.Sqrt(n / max))).ToArray();
        }).ToArray();
    }

    private static int Bin(float v, int bins) => Math.Clamp((int)(v * bins), 0, bins - 1);
}

/// <summary>
/// A <see cref="CurveSet"/> prepared for fast per-pixel evaluation: each channel's curve, composed
/// with the composite curve, is tabulated. Channels whose curves are both identity are left untouched,
/// so their out-of-range values survive.
/// </summary>
public sealed class CurveLookup
{
    private const int Size = 4096; // table intervals, linearly interpolated

    private readonly float[]? _r, _g, _b;

    /// <param name="monochrome">Ignore the per-channel curves, as for a single-channel image.</param>
    public CurveLookup(CurveSet curves, bool monochrome = false)
    {
        var master = curves[CurveChannel.Rgb];
        _r = Table(master, monochrome ? null : curves[CurveChannel.Red]);
        _g = Table(master, monochrome ? null : curves[CurveChannel.Green]);
        _b = Table(master, monochrome ? null : curves[CurveChannel.Blue]);
    }

    public bool IsEmpty => _r is null && _g is null && _b is null;

    private static float[]? Table(ToneCurve master, ToneCurve? channel)
    {
        bool masterIdentity = master.IsIdentity, channelIdentity = channel?.IsIdentity ?? true;
        if (masterIdentity && channelIdentity) return null;

        var first = channelIdentity ? null : new ToneCurve.Spline(channel!.Points);
        var second = masterIdentity ? null : new ToneCurve.Spline(master.Points);
        var table = new float[Size + 1];
        for (int i = 0; i <= Size; i++)
        {
            float v = i / (float)Size;
            if (first is not null) v = first.Evaluate(v);
            if (second is not null) v = second.Evaluate(v);
            table[i] = v;
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(ref float r, ref float g, ref float b)
    {
        if (_r is not null) r = Map(_r, r);
        if (_g is not null) g = Map(_g, g);
        if (_b is not null) b = Map(_b, b);
    }

    /// <summary>Applies the composite curve to a single-channel value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ApplyMono(float v) => _r is null ? v : Map(_r, v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Map(float[] table, float v)
    {
        float pos = Math.Clamp(v, 0f, 1f) * Size;
        int i = (int)pos;
        if (i >= Size) return table[Size];
        return table[i] + (table[i + 1] - table[i]) * (pos - i);
    }
}
