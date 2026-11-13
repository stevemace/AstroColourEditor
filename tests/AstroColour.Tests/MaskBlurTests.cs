using AstroColour.Core;
using AstroColour.Core.Colour;

namespace AstroColour.Tests;

public class MaskBlurTests
{
    [Theory]
    [InlineData(2f)]
    [InlineData(3.5f)]
    [InlineData(20f)]
    public void Box_radii_approximate_the_gaussian_variance(float sigma)
    {
        // A box of radius r has variance ((2r+1)² − 1) / 12; the passes' variances add up.
        double variance = MaskBlur.BoxRadii(sigma).Sum(r => ((2.0 * r + 1) * (2 * r + 1) - 1) / 12);
        Assert.InRange(Math.Sqrt(variance), sigma * 0.85, sigma * 1.15);
    }

    [Fact]
    public void Blur_keeps_a_flat_plane_flat_and_spreads_a_point()
    {
        const int w = 41, h = 31;
        var flat = Enumerable.Repeat(0.4f, w * h).ToArray();
        MaskBlur.Gaussian(flat, w, h, 5f);
        Assert.All(flat, v => Assert.Equal(0.4f, v, 4));

        var point = new float[w * h];
        point[15 * w + 20] = 1f;
        MaskBlur.Gaussian(point, w, h, 3f);
        Assert.Equal(1f, point.Sum(), 3); // nothing lost away from the edges
        Assert.True(point[15 * w + 20] < 0.1f);
        Assert.True(point[15 * w + 23] > 0f && point[18 * w + 20] > 0f);
        Assert.True(point[15 * w + 20] > point[15 * w + 23]);
    }

    [Fact]
    public void Blurred_mask_spreads_the_adjustment_to_neighbouring_pixels()
    {
        // A red square on grey: unblurred, only the red changes; blurred, the grey beside it is lightened too.
        const int w = 40, h = 40;
        var r = new float[w * h]; var g = new float[w * h]; var b = new float[w * h];
        for (int i = 0; i < w * h; i++) { r[i] = g[i] = b[i] = 0.3f; }
        for (int y = 14; y < 26; y++)
            for (int x = 14; x < 26; x++) { int i = y * w + x; r[i] = 0.8f; g[i] = 0.1f; b[i] = 0.1f; }
        var image = new RgbImage(w, h, r, g, b);
        int beside = 20 * w + 27; // three pixels right of the square

        var sharp = new ColourAdjustment { TargetHue = 0, Lightness = 50 };
        var blurred = sharp.Clone();
        blurred.MaskBlur = 4;

        var sharpOut = ColourProcessor.Apply(new TestImage(image), [sharp]);
        var blurredOut = ColourProcessor.Apply(new TestImage(image), [blurred]);
        Assert.Equal(0.3f, sharpOut[0][beside], 5);
        Assert.True(blurredOut[0][beside] > 0.31f);
        Assert.Equal(0.3f, blurredOut[0][0], 5); // far corner untouched

        // A downsampled preview blurs by the same distance in full-size pixels.
        var mask = new AdjustmentSet([blurred], includeAll: true);
        mask.BuildMasks(r, g, b, w, h, null);
        var half = image.Downsample(2);
        var halfMask = new AdjustmentSet([blurred], includeAll: true);
        halfMask.BuildMasks(half.R, half.G, half.B, half.Width, half.Height, null, pixelScale: 2);
        // Half-size pixel (14, 10) covers full-size pixels 28–29 of rows 20–21.
        float fullAverage = (mask.MaskWeight(20 * w + 28, 0, 0, 0) + mask.MaskWeight(20 * w + 29, 0, 0, 0) +
                             mask.MaskWeight(21 * w + 28, 0, 0, 0) + mask.MaskWeight(21 * w + 29, 0, 0, 0)) / 4;
        Assert.InRange(fullAverage, 0.05f, 0.95f);
        Assert.Equal(fullAverage, halfMask.MaskWeight(10 * half.Width + 14, 0, 0, 0), 0.06f);
    }

    [Fact]
    public void Masks_are_reused_until_what_they_depend_on_changes()
    {
        const int w = 30, h = 20;
        var r = new float[w * h]; var g = new float[w * h]; var b = new float[w * h];
        for (int i = 0; i < w * h; i++) { r[i] = (i % w) / (float)w; g[i] = 0.2f; b[i] = 0.3f; }
        var image = new RgbImage(w, h, r, g, b);
        var cache = new MaskCache();
        var a = new ColourAdjustment { TargetHue = 0, Range = 30, MaskBlur = 3, Saturation = 40 };
        var curves = new Core.Curves.CurveSet();
        var pixels = new byte[w * h * 4];

        void Render(float saturation = 0f) =>
            ColourProcessor.RenderPreview(image, [a], curves, PreviewMode.Adjusted, null, pixels, saturation: saturation, maskCache: cache);

        Render();
        Assert.Equal(1, cache.Count);
        var withCache = (byte[])pixels.Clone();

        a.Saturation = -40; // the change made, not the selection
        Render();
        Assert.Equal(1, cache.Count);

        a.Saturation = 40;
        Render();
        Assert.Equal(withCache, pixels);
        ColourProcessor.RenderPreview(image, [a], curves, PreviewMode.Adjusted, null, pixels); // no cache
        Assert.Equal(withCache, pixels);

        a.Range = 20; // the selection
        Render();
        Assert.Equal(2, cache.Count);

        curves[Core.Curves.CurveChannel.Rgb].Points.Insert(1, new(0.5f, 0.6f)); // an edit before the colours
        Render();
        Assert.Equal(3, cache.Count);

        Render(saturation: 20);
        Assert.Equal(4, cache.Count);

        // Two adjustments with the same selection share one mask.
        cache.Clear();
        ColourProcessor.RenderPreview(image, [a, a.Clone()], curves, PreviewMode.Adjusted, null, pixels, maskCache: cache);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Cache_drops_the_oldest_masks_beyond_its_size()
    {
        var image = new RgbImage(10, 10, new float[100], new float[100], new float[100]);
        var cache = new MaskCache(maxPixels: 250);
        var pixels = new byte[400];
        for (int i = 1; i <= 4; i++)
            ColourProcessor.RenderPreview(image, [new ColourAdjustment { MaskBlur = i, Lightness = 10 }], null,
                PreviewMode.Adjusted, null, pixels, maskCache: cache);
        Assert.Equal(2, cache.Count);
    }

    private sealed class TestImage(RgbImage image) : IAstroImage
    {
        public int Width => image.Width;
        public int Height => image.Height;
        public int Channels => 3;
        public string ColorSpace => "RGB";
        public string SampleFormatName => "Float32";
        public float[][] Planes => [image.R, image.G, image.B];
        public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None, CropRect? crop = null) => throw new NotSupportedException();
    }
}
