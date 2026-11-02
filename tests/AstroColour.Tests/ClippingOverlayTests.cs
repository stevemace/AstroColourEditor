using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Tests;

public class ClippingOverlayTests
{
    private const uint Red = 0xFFFF0000u, Blue = 0xFF0000FFu, Magenta = 0xFFFF00FFu;

    private static uint[] Render(RgbImage image, CurveSet curves, bool showClipping, out ClippingStats stats,
        PreviewMode mode = PreviewMode.Adjusted)
    {
        var bgra = new byte[image.PixelCount * 4];
        stats = ColourProcessor.RenderPreview(image, [], curves, mode, null, bgra, showClipping: showClipping);
        var pixels = new uint[image.PixelCount];
        Buffer.BlockCopy(bgra, 0, pixels, 0, bgra.Length);
        return pixels;
    }

    /// <summary>A curve that makes everything below 0.2 black and everything above 0.8 white.</summary>
    private static CurveSet Contrast()
    {
        var curves = new CurveSet();
        curves[CurveChannel.Rgb].Points.Clear();
        curves[CurveChannel.Rgb].Points.AddRange([new(0.2f, 0f), new(0.8f, 1f)]);
        return curves;
    }

    [Fact]
    public void Pixels_pushed_to_black_or_white_are_marked()
    {
        var image = new RgbImage(4, 1,
            [0.1f, 0.9f, 0.5f, 0.1f],
            [0.1f, 0.9f, 0.5f, 0.5f],
            [0.1f, 0.9f, 0.5f, 0.9f]);
        var pixels = Render(image, Contrast(), showClipping: true, out var stats);

        Assert.Equal(Blue, pixels[0]);
        Assert.Equal(Red, pixels[1]);
        Assert.NotEqual(Red, pixels[2]);
        Assert.NotEqual(Blue, pixels[2]);
        Assert.Equal(Magenta, pixels[3]); // red channel to black, blue channel to white
        Assert.Equal(0.5, stats.Shadows, 6);
        Assert.Equal(0.5, stats.Highlights, 6);
    }

    [Fact]
    public void Channels_already_black_or_white_are_not_marked()
    {
        var image = new RgbImage(2, 1, [0f, 1f], [0f, 1f], [0f, 1f]);
        var pixels = Render(image, Contrast(), showClipping: true, out var stats);
        Assert.Equal(0xFF000000u, pixels[0]);
        Assert.Equal(0xFFFFFFFFu, pixels[1]);
        Assert.Equal(default, stats);
    }

    [Fact]
    public void Overlay_is_off_unless_asked_for_and_only_in_the_adjusted_view()
    {
        var image = new RgbImage(1, 1, [0.1f], [0.1f], [0.1f]);
        Assert.Equal(0xFF000000u, Render(image, Contrast(), showClipping: false, out var off)[0]);
        Assert.Equal(default, off);
        Assert.NotEqual(Blue, Render(image, Contrast(), showClipping: true, out var original, PreviewMode.Original)[0]);
        Assert.Equal(default, original);
    }

    [Fact]
    public void Colour_edits_that_clip_are_marked_too()
    {
        // Doubling saturation pushes a dark red's green and blue to black.
        var image = new RgbImage(1, 1, [0.5f], [0.1f], [0.1f]);
        var bgra = new byte[4];
        var stats = ColourProcessor.RenderPreview(image, [], null, PreviewMode.Adjusted, null, bgra, saturation: 100f, showClipping: true);
        Assert.Equal(1.0, stats.Shadows);
    }
}
