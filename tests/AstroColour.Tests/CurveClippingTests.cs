using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Tests;

public class CurveClippingTests
{
    // Four pixels: dark, mid grey, a star bright only in red, and one already white.
    private static readonly CurveClipping Sample = CurveClipping.Sample(new RgbImage(4, 1,
        [0.05f, 0.5f, 0.95f, 1f],
        [0.05f, 0.5f, 0.6f, 1f],
        [0.05f, 0.5f, 0.6f, 1f]));

    [Fact]
    public void Identity_curves_clip_nothing()
    {
        Assert.Equal((0d, 0d), Sample.Measure(new CurveSet(), CurveChannel.Rgb));
    }

    [Fact]
    public void Moving_end_points_counts_newly_clipped_pixels_only()
    {
        var curves = new CurveSet();
        curves[CurveChannel.Rgb].Points[0] = new(0.1f, 0f);
        curves[CurveChannel.Rgb].Points[1] = new(0.9f, 1f);

        var (shadows, highlights) = Sample.Measure(curves, CurveChannel.Rgb);
        Assert.Equal(0.25, shadows);    // the dark pixel
        Assert.Equal(0.25, highlights); // the red star; the white pixel was already white
    }

    [Fact]
    public void Rgb_counts_a_pixel_if_any_channel_clips()
    {
        var curves = new CurveSet();
        curves[CurveChannel.Red].Points[1] = new(0.9f, 1f);

        Assert.Equal(0.25, Sample.Measure(curves, CurveChannel.Rgb).Highlights);
        Assert.Equal(0.25, Sample.Measure(curves, CurveChannel.Red).Highlights);
        Assert.Equal(0, Sample.Measure(curves, CurveChannel.Green).Highlights);
        Assert.Equal(0, Sample.Measure(curves, CurveChannel.Rgb, monochrome: true).Highlights);
    }
}
