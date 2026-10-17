using AstroColour.Core;
using AstroColour.Core.Colour;

namespace AstroColour.Tests;

public class NarrowbandNormalizationTests
{
    private const int W = 40, H = 25;

    /// <summary>A green-heavy SHO-like image: each channel is a ramp with its own background and signal strength.</summary>
    private static RgbImage ShoImage()
    {
        var r = new float[W * H];
        var g = new float[W * H];
        var b = new float[W * H];
        var random = new Random(1);
        for (int i = 0; i < r.Length; i++)
        {
            float t = (float)random.NextDouble();
            r[i] = 0.05f + 0.10f * t * t;  // faint SII
            g[i] = 0.20f + 0.50f * t;      // strong Hα
            b[i] = 0.10f + 0.20f * (float)Math.Sqrt(t); // OIII
        }
        return new RgbImage(W, H, r, g, b);
    }

    private static RgbImage Normalise(RgbImage image, NarrowbandSettings settings)
    {
        var nb = NarrowbandNormalization.Create(settings, ChannelStatistics.Measure(image))!;
        var r = new float[image.PixelCount];
        var g = new float[image.PixelCount];
        var b = new float[image.PixelCount];
        for (int i = 0; i < image.PixelCount; i++)
        {
            float pr = image.R[i], pg = image.G[i], pb = image.B[i];
            nb.Apply(ref pr, ref pg, ref pb);
            (r[i], g[i], b[i]) = (pr, pg, pb);
        }
        return new RgbImage(image.Width, image.Height, r, g, b);
    }

    private static (float R, float G, float B) NormalisePixel(NarrowbandNormalization nb, float r, float g, float b)
    {
        nb.Apply(ref r, ref g, ref b);
        return (r, g, b);
    }

    [Fact]
    public void Measures_median_and_average_positive_deviation()
    {
        float[] values = [0.1f, 0.2f, 0.3f, 0.4f, 0.9f];
        var stats = ChannelStatistics.Measure(new RgbImage(5, 1, values, values, values));
        Assert.Equal(0.3f, stats.Median[0], 1e-4f);
        // Only the pixels above the median count: (0.1 + 0.6) / 2.
        Assert.Equal(0.35f, stats.PositiveDeviation[1], 1e-4f);
    }

    [Fact]
    public void Mtf_matches_pixinsight()
    {
        Assert.Equal(0.3f, NarrowbandNormalization.Mtf(0.5f, 0.3f), 1e-6f);
        Assert.Equal(0.5f, NarrowbandNormalization.Mtf(0.25f, 0.25f), 1e-6f); // m maps to 0.5
        Assert.Equal(0f, NarrowbandNormalization.Mtf(0.1f, 0f));
        Assert.Equal(1f, NarrowbandNormalization.Mtf(0.1f, 1f));
    }

    [Fact]
    public void Sho_stretches_sii_and_oiii_to_the_h_alpha_positive_deviation()
    {
        var image = ShoImage();
        var stats = ChannelStatistics.Measure(image);
        var nb = NarrowbandNormalization.Create(new NarrowbandSettings(NarrowbandPalette.Sho), stats)!;
        float Unscreened(int c) => stats.PositiveDeviation[c] / (1 - stats.Median[c]);

        // A pixel one average positive deviation above each channel's median lands where Hα's does, unscreened.
        float[] probe = [stats.Median[0] + stats.PositiveDeviation[0], 0.5f, stats.Median[2] + stats.PositiveDeviation[2]];
        var (r, g, b) = NormalisePixel(nb, probe[0], probe[1], probe[2]);
        Assert.Equal(stats.Median[0] + Unscreened(1) * (1 - stats.Median[0]), r, 1e-4f);
        Assert.Equal(stats.Median[2] + Unscreened(1) * (1 - stats.Median[2]), b, 1e-4f);
        Assert.Equal(0.5f, g); // Hα itself is untouched

        // The background point stays put, and anything darker is left alone.
        Assert.Equal(stats.Median[0], NormalisePixel(nb, stats.Median[0], 0.5f, 0.5f).R, 1e-6f);
        Assert.Equal(stats.Median[0] - 0.01f, NormalisePixel(nb, stats.Median[0] - 0.01f, 0.5f, 0.5f).R);
        Assert.Equal(1f, NormalisePixel(nb, 1f, 0.5f, 1f).R, 1e-6f);

        // The faint SII channel is lifted; the stretch keeps every value in range without clipping.
        var after = Normalise(image, new NarrowbandSettings(NarrowbandPalette.Sho));
        Assert.True(ChannelStatistics.Measure(after).PositiveDeviation[0] > stats.PositiveDeviation[0]);
        Assert.Equal(stats.Median[0], ChannelStatistics.Measure(after).Median[0], 1e-4f);
    }

    [Fact]
    public void Hoo_stretches_both_oiii_channels_to_h_alpha_in_red()
    {
        var sho = ShoImage();
        var hoo = new RgbImage(W, H, sho.G, sho.B, sho.B); // Hα red, OIII green and blue
        var stats = ChannelStatistics.Measure(hoo);
        var nb = NarrowbandNormalization.Create(new NarrowbandSettings(NarrowbandPalette.Hoo), stats)!;
        float target = stats.PositiveDeviation[0] / (1 - stats.Median[0]);

        float oiii = stats.Median[1] + stats.PositiveDeviation[1];
        var (r, g, b) = NormalisePixel(nb, 0.5f, oiii, oiii);
        Assert.Equal(0.5f, r);
        Assert.Equal(stats.Median[1] + target * (1 - stats.Median[1]), g, 1e-4f);
        Assert.Equal(g, b, 1e-6f);
    }

    [Fact]
    public void Boosts_scale_the_normalised_channels()
    {
        var image = ShoImage();
        var plain = Normalise(image, new NarrowbandSettings(NarrowbandPalette.Sho));
        var boosted = Normalise(image, new NarrowbandSettings(NarrowbandPalette.Sho, SiiBoost: 1.5f, OiiiBoost: 0.5f));
        for (int i = 0; i < image.PixelCount; i++)
        {
            Assert.Equal(Math.Min(1f, plain.R[i] * 1.5f), boosted.R[i], 1e-5f);
            Assert.Equal(plain.G[i], boosted.G[i], 1e-6f);
            Assert.Equal(plain.B[i] * 0.5f, boosted.B[i], 1e-5f);
        }
    }

    [Fact]
    public void Full_green_reduction_caps_green_at_the_red_blue_average()
    {
        var image = ShoImage();
        var plain = Normalise(image, new NarrowbandSettings(NarrowbandPalette.Sho));
        var reduced = Normalise(image, new NarrowbandSettings(NarrowbandPalette.Sho, GreenReduction: 1f));
        for (int i = 0; i < image.PixelCount; i++)
            Assert.Equal(Math.Min(plain.G[i], (plain.R[i] + plain.B[i]) / 2), reduced.G[i], 1e-6f);
    }

    [Theory]
    [InlineData(NarrowbandLightness.Original)]
    [InlineData(NarrowbandLightness.HAlpha)]
    public void Lightness_options_set_each_pixels_lightness(NarrowbandLightness mode)
    {
        var image = ShoImage();
        var result = Normalise(image, new NarrowbandSettings(NarrowbandPalette.Sho, Lightness: mode));
        for (int i = 0; i < image.PixelCount; i++)
        {
            ColourMath.RgbToHsl(image.R[i], image.G[i], image.B[i], out _, out _, out float original, out _);
            ColourMath.RgbToHsl(result.R[i], result.G[i], result.B[i], out _, out _, out float l, out _);
            Assert.Equal(mode == NarrowbandLightness.HAlpha ? image.G[i] : original, l, 1e-4f);
        }
    }

    [Fact]
    public void Off_creates_no_normalisation()
    {
        Assert.Null(NarrowbandNormalization.Create(new NarrowbandSettings(), ChannelStatistics.Measure(ShoImage())));
        Assert.Null(NarrowbandNormalization.Create(new NarrowbandSettings(NarrowbandPalette.Sho), null));
    }

    [Fact]
    public void Saving_and_preview_apply_it_but_before_view_does_not()
    {
        var rgb = ShoImage();
        var nb = NarrowbandNormalization.Create(new NarrowbandSettings(NarrowbandPalette.Sho), ChannelStatistics.Measure(rgb));
        var expected = Normalise(rgb, new NarrowbandSettings(NarrowbandPalette.Sho));

        var saved = ColourProcessor.Apply(new TestImage(rgb), [], narrowband: nb);
        Assert.Equal(expected.R, saved[0]);
        Assert.Equal(expected.B, saved[2]);

        var adjusted = new byte[rgb.PixelCount * 4];
        var original = new byte[rgb.PixelCount * 4];
        ColourProcessor.RenderPreview(rgb, [], null, PreviewMode.Adjusted, null, adjusted, narrowband: nb);
        ColourProcessor.RenderPreview(rgb, [], null, PreviewMode.Original, null, original, narrowband: nb);
        Assert.Equal((byte)(expected.R[0] * 255 + 0.5f), adjusted[2]); // BGRA
        Assert.Equal((byte)(rgb.R[0] * 255 + 0.5f), original[2]);
    }

    private sealed class TestImage(RgbImage rgb) : IAstroImage
    {
        public int Width => rgb.Width;
        public int Height => rgb.Height;
        public int Channels => 3;
        public string ColorSpace => "RGB";
        public string SampleFormatName => "Float32";
        public float[][] Planes { get; } = [rgb.R, rgb.G, rgb.B];
        public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None) => throw new NotSupportedException();
    }
}
