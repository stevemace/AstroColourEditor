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

/// <summary>The median and average absolute deviation from it (PixInsight's med and adev) of each colour channel.</summary>
public sealed record ChannelStatistics(float[] Median, float[] AverageDeviation)
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
            foreach (float v in planes[c]) sum += Math.Abs(Math.Clamp(v, 0f, 1f) - median[c]);
            deviation[c] = planes[c].Length == 0 ? 0f : (float)(sum / planes[c].Length);
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
/// Narrowband normalisation after Bill Blanshan's PixelMath: each non-Hα channel is matched to the Hα channel's
/// background level and spread, <c>(adev(Hα) / adev(c)) × (c − med(c)) + med(Hα)</c>, which tames the
/// strong Hα signal (the green cast of SHO images). The boosts, SCNR (average neutral) green reduction and
/// lightness options then follow. Results are truncated to [0,1], as PixelMath does.
/// </summary>
public sealed class NarrowbandNormalization
{
    private readonly NarrowbandSettings _settings;
    // Per channel: normalised = value × scale + offset, then × boost.
    private readonly float[] _scale = [1, 1, 1], _offset = [0, 0, 0], _boost = [1, 1, 1];
    private readonly int _hAlpha;

    public NarrowbandNormalization(NarrowbandSettings settings, ChannelStatistics statistics)
    {
        _settings = settings;
        _hAlpha = settings.Palette == NarrowbandPalette.Hoo ? 0 : 1;
        float referenceMedian = statistics.Median[_hAlpha], referenceDeviation = statistics.AverageDeviation[_hAlpha];
        for (int c = 0; c < 3; c++)
        {
            if (c == _hAlpha) continue;
            float deviation = statistics.AverageDeviation[c];
            _scale[c] = deviation > 1e-7f && referenceDeviation > 1e-7f ? referenceDeviation / deviation : 1f;
            _offset[c] = referenceMedian - _scale[c] * statistics.Median[c];
            _boost[c] = settings.Palette == NarrowbandPalette.Sho && c == 0 ? settings.SiiBoost : settings.OiiiBoost;
        }
    }

    /// <summary>Null when the settings leave the image unchanged.</summary>
    public static NarrowbandNormalization? Create(NarrowbandSettings settings, ChannelStatistics? statistics) =>
        settings.IsEmpty || statistics is null ? null : new NarrowbandNormalization(settings, statistics);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(ref float r, ref float g, ref float b)
    {
        float r0 = Math.Clamp(r, 0f, 1f), g0 = Math.Clamp(g, 0f, 1f), b0 = Math.Clamp(b, 0f, 1f);
        float nr = Math.Clamp((r0 * _scale[0] + _offset[0]) * _boost[0], 0f, 1f);
        float ng = Math.Clamp((g0 * _scale[1] + _offset[1]) * _boost[1], 0f, 1f);
        float nb = Math.Clamp((b0 * _scale[2] + _offset[2]) * _boost[2], 0f, 1f);

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
