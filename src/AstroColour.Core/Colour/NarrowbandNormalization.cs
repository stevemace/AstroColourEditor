using System.Runtime.CompilerServices;

namespace AstroColour.Core.Colour;

/// <summary>How the narrowband channels are mapped to colour.</summary>
public enum NarrowbandPalette
{
    Off,
    /// <summary>SII in red, Hα in green, OIII in blue.</summary>
    Sho,
    /// <summary>Hα in red, OIII in green and blue.</summary>
    Hoo,
}

/// <summary>What sets each pixel's lightness after normalising.</summary>
public enum NarrowbandLightness
{
    /// <summary>The lightness of the normalised colour.</summary>
    Normalised,
    /// <summary>The lightness the pixel had before normalising.</summary>
    Original,
    /// <summary>The Hα signal.</summary>
    HAlpha,
}

/// <summary>Settings for <see cref="NarrowbandNormalization"/>.</summary>
/// <param name="SiiBoost">Multiplies the normalised SII channel (SHO only), 0.5–2.</param>
/// <param name="OiiiBoost">Multiplies the normalised OIII channel(s), 0.5–2.</param>
/// <param name="GreenReduction">How much SCNR (average neutral) green removal to apply, 0–1.</param>
public sealed record NarrowbandSettings(
    NarrowbandPalette Palette = NarrowbandPalette.Off,
    float SiiBoost = 1f,
    float OiiiBoost = 1f,
    float GreenReduction = 0f,
    NarrowbandLightness Lightness = NarrowbandLightness.Normalised)
{
    public bool IsEmpty => Palette == NarrowbandPalette.Off;
}

/// <summary>
/// The median of each colour channel, and the average positive deviation from it: the mean of
/// <c>v − median</c> over the pixels brighter than the median.
/// </summary>
public sealed record ChannelStatistics(float[] Median, float[] PositiveDeviation)
{
    /// <summary>Measures every pixel of the image's three channels, with values clamped to [0,1].</summary>
    public static ChannelStatistics Measure(RgbImage image)
    {
        float[][] planes = [image.R, image.G, image.B];
        var median = new float[3];
        var deviation = new float[3];
        Parallel.For(0, 3, c =>
        {
            median[c] = MedianOf(planes[c]);
            double sum = 0;
            long count = 0;
            foreach (float v in planes[c])
            {
                float d = Math.Clamp(v, 0f, 1f) - median[c];
                if (d <= 0f) continue;
                sum += d;
                count++;
            }
            deviation[c] = count == 0 ? 0f : (float)(sum / count);
        });
        return new ChannelStatistics(median, deviation);
    }

    /// <summary>The median to 1/65536, found from a histogram.</summary>
    private static float MedianOf(float[] plane)
    {
        const int bins = 65536;
        var counts = new long[bins];
        foreach (float v in plane) counts[(int)(Math.Clamp(v, 0f, 1f) * (bins - 1) + 0.5f)]++;
        long half = (plane.Length + 1) / 2, seen = 0;
        for (int i = 0; i < bins; i++)
        {
            seen += counts[i];
            if (seen >= half) return i / (float)(bins - 1);
        }
        return 0f;
    }
}

/// <summary>
/// Narrowband normalisation as in PixInsight's NarrowbandNormalization process, with the shadow point at 1:
/// each non-Hα channel's background point is its median, and the signal above it is stretched with a midtones
/// transfer function so that the channel's average positive deviation from its median, unscreened from the
/// background, matches the Hα channel's. Values below the background are left alone. This tames the strong Hα
/// signal (the green cast of SHO images). The boosts, SCNR (average neutral) green reduction and lightness
/// options then follow.
/// </summary>
public sealed class NarrowbandNormalization
{
    private readonly NarrowbandSettings _settings;
    // Per channel: background point, midtones balance (0.5 leaves the channel unchanged) and boost.
    private readonly float[] _background = [0, 0, 0], _midtones = [0.5f, 0.5f, 0.5f], _boost = [1, 1, 1];
    private readonly int _hAlpha;

    /// <summary>The settings this normalisation was made from.</summary>
    public NarrowbandSettings Settings => _settings;

    public NarrowbandNormalization(NarrowbandSettings settings, ChannelStatistics statistics)
    {
        _settings = settings;
        _hAlpha = settings.Palette == NarrowbandPalette.Hoo ? 0 : 1;
        float reference = Unscreened(statistics, _hAlpha);
        for (int c = 0; c < 3; c++)
        {
            if (c == _hAlpha) continue;
            _background[c] = statistics.Median[c];
            _midtones[c] = MidtonesBalance(Unscreened(statistics, c), reference);
            _boost[c] = settings.Palette == NarrowbandPalette.Sho && c == 0 ? settings.SiiBoost : settings.OiiiBoost;
        }
    }

    /// <summary>A channel's average positive deviation, unscreened from its background (the median).</summary>
    private static float Unscreened(ChannelStatistics statistics, int c)
    {
        float background = statistics.Median[c];
        return background >= 1f ? 0f : Math.Clamp(statistics.PositiveDeviation[c] / (1f - background), 0f, 1f);
    }

    /// <summary>The midtones balance m for which mtf(m, <paramref name="from"/>) = <paramref name="to"/>.</summary>
    private static float MidtonesBalance(float from, float to)
    {
        if (from <= 1e-7f || to <= 1e-7f || from >= 1f - 1e-7f || to >= 1f - 1e-7f) return 0.5f;
        return from * (1f - to) / (from - 2f * from * to + to);
    }

    /// <summary>PixInsight's midtones transfer function.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Mtf(float m, float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;
        return (m - 1f) * x / ((2f * m - 1f) * x - m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Normalise(float v, int c)
    {
        float background = _background[c];
        if (_midtones[c] == 0.5f || v < background) return v;
        float stretched = Mtf(_midtones[c], (v - background) / (1f - background));
        return 1f - (1f - stretched) * (1f - background);
    }

    /// <summary>Null when the settings leave the image unchanged.</summary>
    public static NarrowbandNormalization? Create(NarrowbandSettings settings, ChannelStatistics? statistics) =>
        settings.IsEmpty || statistics is null ? null : new NarrowbandNormalization(settings, statistics);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(ref float r, ref float g, ref float b)
    {
        float r0 = Math.Clamp(r, 0f, 1f), g0 = Math.Clamp(g, 0f, 1f), b0 = Math.Clamp(b, 0f, 1f);
        float nr = Math.Clamp(Normalise(r0, 0) * _boost[0], 0f, 1f);
        float ng = Math.Clamp(Normalise(g0, 1) * _boost[1], 0f, 1f);
        float nb = Math.Clamp(Normalise(b0, 2) * _boost[2], 0f, 1f);

        if (_settings.GreenReduction > 0f)
        {
            float neutral = Math.Min(ng, (nr + nb) * 0.5f);
            ng += (neutral - ng) * _settings.GreenReduction;
        }

        if (_settings.Lightness != NarrowbandLightness.Normalised)
        {
            float lightness;
            if (_settings.Lightness == NarrowbandLightness.HAlpha)
                lightness = _hAlpha == 0 ? r0 : g0;
            else
                ColourMath.RgbToHsl(r0, g0, b0, out _, out _, out lightness, out _);
            ColourMath.RgbToHsl(nr, ng, nb, out float h, out float s, out _, out _);
            ColourMath.HslToRgb(h, s, lightness, out nr, out ng, out nb);
        }

        r = nr;
        g = ng;
        b = nb;
    }
}
