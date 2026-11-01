using AstroColour.Core.Colour;

namespace AstroColour.Core.Curves;

/// <summary>
/// A sample of an image's pixels for measuring how many of them a <see cref="CurveSet"/> clips to black or
/// white. Only pixels the curves clip are counted: a channel already at black or white before them is not.
/// </summary>
public sealed class CurveClipping
{
    private const int Levels = 4096;              // inputs are quantised to this many levels
    private const float Threshold = 0.5f / 255f;  // outputs this close to 0 or 1 count as clipped
    private const int MaxSamples = 500_000;

    private const byte Black = 1, White = 2;

    private readonly ushort[] _r, _g, _b;

    private CurveClipping(ushort[] r, ushort[] g, ushort[] b) => (_r, _g, _b) = (r, g, b);

    /// <param name="baseAdjustments">If given, the sample is of the image after these.</param>
    public static CurveClipping Sample(RgbImage image, BaseAdjustments? baseAdjustments = null)
    {
        int step = Math.Max(1, image.PixelCount / MaxSamples);
        int count = (image.PixelCount + step - 1) / step;
        var r = new ushort[count];
        var g = new ushort[count];
        var b = new ushort[count];
        for (int i = 0, j = 0; j < count; i += step, j++)
        {
            float pr = image.R[i], pg = image.G[i], pb = image.B[i];
            baseAdjustments?.Apply(ref pr, ref pg, ref pb);
            (r[j], g[j], b[j]) = (Quantise(pr), Quantise(pg), Quantise(pb));
        }
        return new CurveClipping(r, g, b);
    }

    /// <summary>
    /// The fractions of pixels that <paramref name="curves"/> clips to black and to white. For
    /// <see cref="CurveChannel.Rgb"/> a pixel counts if any of its channels clips, through the composite and
    /// that channel's curve; for a single channel, only that channel counts.
    /// </summary>
    /// <param name="monochrome">Ignore the per-channel curves, as for a single-channel image.</param>
    public (double Shadows, double Highlights) Measure(CurveSet curves, CurveChannel channel, bool monochrome = false)
    {
        if (_r.Length == 0) return (0, 0);
        var master = curves[CurveChannel.Rgb];
        if (monochrome) return Count(Flags(master, null), null, null, _r, _g, _b);

        var r = channel is CurveChannel.Rgb or CurveChannel.Red ? Flags(master, curves[CurveChannel.Red]) : null;
        var g = channel is CurveChannel.Rgb or CurveChannel.Green ? Flags(master, curves[CurveChannel.Green]) : null;
        var b = channel is CurveChannel.Rgb or CurveChannel.Blue ? Flags(master, curves[CurveChannel.Blue]) : null;
        return Count(r, g, b, _r, _g, _b);
    }

    private static (double, double) Count(byte[]? fr, byte[]? fg, byte[]? fb, ushort[] r, ushort[] g, ushort[] b)
    {
        if (fr is null && fg is null && fb is null) return (0, 0);
        long shadows = 0, highlights = 0;
        for (int i = 0; i < r.Length; i++)
        {
            int f = (fr?[r[i]] ?? 0) | (fg?[g[i]] ?? 0) | (fb?[b[i]] ?? 0);
            shadows += f & Black;
            highlights += (f & White) >> 1;
        }
        return (shadows / (double)r.Length, highlights / (double)r.Length);
    }

    /// <summary>For each input level, whether the curves newly clip it to black or white; null if they are identity.</summary>
    private static byte[]? Flags(ToneCurve master, ToneCurve? channel)
    {
        bool masterIdentity = master.IsIdentity, channelIdentity = channel?.IsIdentity ?? true;
        if (masterIdentity && channelIdentity) return null;

        var first = channelIdentity ? null : new ToneCurve.Spline(channel!.Points);
        var second = masterIdentity ? null : new ToneCurve.Spline(master.Points);
        var flags = new byte[Levels];
        for (int i = 0; i < Levels; i++)
        {
            float input = i / (float)(Levels - 1), v = input;
            if (first is not null) v = first.Evaluate(v);
            if (second is not null) v = second.Evaluate(v);
            if (v <= Threshold && input > Threshold) flags[i] |= Black;
            if (v >= 1f - Threshold && input < 1f - Threshold) flags[i] |= White;
        }
        return flags;
    }

    private static ushort Quantise(float v) => (ushort)(Math.Clamp(v, 0f, 1f) * (Levels - 1) + 0.5f);
}
