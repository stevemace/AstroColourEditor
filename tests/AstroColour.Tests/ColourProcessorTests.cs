using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Tests;

public class ColourProcessorTests
{
    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0.2f, 0.6f, 0.9f)]
    [InlineData(0.8f, 0.75f, 0.1f)]
    [InlineData(0.3f, 0.3f, 0.3f)]
    public void Hsl_round_trips(float r, float g, float b)
    {
        ColourMath.RgbToHsl(r, g, b, out float h, out float s, out float l, out _);
        ColourMath.HslToRgb(h, s, l, out float r2, out float g2, out float b2);
        Assert.Equal(r, r2, 4);
        Assert.Equal(g, g2, 4);
        Assert.Equal(b, b2, 4);
    }

    [Fact]
    public void Weight_is_full_inside_range_and_fades_to_zero()
    {
        var a = new ColourAdjustment { TargetHue = 350, Range = 20, Smoothness = 20, NeutralProtection = 0 };
        Assert.Equal(1f, a.Weight(5, 0.5f, 0.5f));          // wraps around 0 degrees
        Assert.InRange(a.Weight(20, 0.5f, 0.5f), 0.01f, 0.99f);
        Assert.Equal(0f, a.Weight(60, 0.5f, 0.5f));
    }

    [Fact]
    public void Neutral_pixels_are_protected()
    {
        var a = new ColourAdjustment { TargetHue = 0, NeutralProtection = 10 };
        Assert.Equal(0f, a.Weight(0, 0f, 0.5f));
        Assert.True(a.Weight(0, 0.05f, 0.5f) is > 0f and < 1f);
        Assert.Equal(1f, a.Weight(0, 0.2f, 0.5f));
    }

    [Fact]
    public void Adjusts_only_selected_colours()
    {
        var set = new AdjustmentSet([new ColourAdjustment { TargetHue = 0, Range = 15, Smoothness = 10, Saturation = -100 }]);

        float r = 0.8f, g = 0.2f, b = 0.2f; // red: selected and fully desaturated
        Assert.True(set.Adjust(ref r, ref g, ref b));
        Assert.Equal(r, g, 4);
        Assert.Equal(g, b, 4);

        float r2 = 0.2f, g2 = 0.3f, b2 = 0.9f; // blue: untouched
        Assert.False(set.Adjust(ref r2, ref g2, ref b2));
        Assert.Equal((0.2f, 0.3f, 0.9f), (r2, g2, b2));
    }

    [Fact]
    public void Colour_curves_apply_only_to_selected_colours()
    {
        var adjustment = new ColourAdjustment { TargetHue = 0, Range = 15, Smoothness = 10 };
        adjustment.Curves[CurveChannel.Rgb].Points[1] = new CurvePoint(1f, 0.5f); // halve every channel
        Assert.True(adjustment.HasEdits);
        Assert.False(adjustment.HasHslEdits);
        var set = new AdjustmentSet([adjustment]);

        float r = 0.8f, g = 0.2f, b = 0.2f; // red: selected
        Assert.True(set.Adjust(ref r, ref g, ref b));
        Assert.Equal(0.4f, r, 3);
        Assert.Equal(0.1f, g, 3);
        Assert.Equal(0.1f, b, 3);

        float r2 = 0.2f, g2 = 0.3f, b2 = 0.9f; // blue: untouched
        Assert.False(set.Adjust(ref r2, ref g2, ref b2));
        Assert.Equal((0.2f, 0.3f, 0.9f), (r2, g2, b2));
    }

    [Fact]
    public void Colour_curves_follow_hsl_changes_and_are_blended_by_weight()
    {
        var adjustment = new ColourAdjustment { TargetHue = 0, Range = 0, Smoothness = 60, NeutralProtection = 0, Saturation = -100 };
        adjustment.Curves[CurveChannel.Rgb].Points[1] = new CurvePoint(1f, 0.5f);
        var set = new AdjustmentSet([adjustment]);

        // Fully selected: desaturated to grey (lightness 0.5), then halved.
        float r = 0.8f, g = 0.2f, b = 0.2f;
        set.Adjust(ref r, ref g, ref b);
        Assert.Equal(0.25f, r, 3);
        Assert.Equal(0.25f, b, 3);

        // Partly selected (30° from the target): the curve moves it only part of the way.
        float r2 = 0.8f, g2 = 0.5f, b2 = 0.2f;
        set.Adjust(ref r2, ref g2, ref b2);
        float weight = adjustment.Weight(30, 0.6f, 0.5f);
        Assert.InRange(weight, 0.1f, 0.9f);
        Assert.InRange(r2, 0.4f, 0.8f);
    }

    [Fact]
    public void Clone_copies_curves()
    {
        var adjustment = new ColourAdjustment();
        var copy = adjustment.Clone();
        copy.Curves[CurveChannel.Red].Points[1] = new CurvePoint(1f, 0.5f);
        Assert.True(adjustment.Curves.IsIdentity);
        Assert.False(copy.Curves.IsIdentity);
    }

    [Fact]
    public void Selection_histogram_counts_only_selected_colours()
    {
        float[] r = [0.9f, 0.1f], g = [0.1f, 0.1f], b = [0.1f, 0.9f]; // a red and a blue pixel
        var histogram = CurveSet.Histogram(new RgbImage(2, 1, r, g, b), bins: 4,
            selection: new ColourAdjustment { TargetHue = 0, Range = 15, Smoothness = 10 });
        Assert.Equal([0f, 0f, 0f, 1f], histogram[(int)CurveChannel.Red]);
        Assert.Equal([1f, 0f, 0f, 0f], histogram[(int)CurveChannel.Blue]);
    }

    [Fact]
    public void Hue_shift_rotates_selected_colour()
    {
        var set = new AdjustmentSet([new ColourAdjustment { TargetHue = 0, HueShift = 120 }]);
        float r = 0.9f, g = 0.1f, b = 0.1f;
        set.Adjust(ref r, ref g, ref b);
        Assert.Equal(120f, ColourMath.Hue(r, g, b), 1);
    }

    [Fact]
    public void Tabulated_weights_match_exact_weights()
    {
        var a = new ColourAdjustment
        {
            TargetHue = 203.7f, Range = 13, Smoothness = 31, NeutralProtection = 8,
            LightnessMin = 20, LightnessMax = 70, LightnessSmoothness = 15, HueShift = 1,
        };
        var set = new AdjustmentSet([a]);
        for (float hue = 0; hue < 360; hue += 0.37f)
            foreach (float chroma in new[] { 0.01f, 0.05f, 0.5f })
                foreach (float lightness in new[] { 0.05f, 0.15f, 0.5f, 0.8f, 0.9f })
                    Assert.Equal(a.Weight(hue, chroma, lightness), set.Weight(hue, chroma, lightness), 0.01f);
    }

    [Fact]
    public void Lightness_range_limits_selection()
    {
        var a = new ColourAdjustment { TargetHue = 0, NeutralProtection = 0, LightnessMin = 30, LightnessMax = 60, LightnessSmoothness = 10 };
        Assert.Equal(1f, a.Weight(0, 0.5f, 0.45f));
        Assert.InRange(a.Weight(0, 0.5f, 0.25f), 0.01f, 0.99f);
        Assert.InRange(a.Weight(0, 0.5f, 0.65f), 0.01f, 0.99f);
        Assert.Equal(0f, a.Weight(0, 0.5f, 0.1f));
        Assert.Equal(0f, a.Weight(0, 0.5f, 0.8f));

        var set = new AdjustmentSet([new ColourAdjustment { TargetHue = 0, LightnessMax = 40, LightnessSmoothness = 0, Saturation = -100 }]);
        float r = 0.5f, g = 0.1f, b = 0.1f; // dark red (L = 0.3): selected
        Assert.True(set.Adjust(ref r, ref g, ref b));
        float r2 = 1f, g2 = 0.6f, b2 = 0.6f; // light red (L = 0.8): untouched
        Assert.False(set.Adjust(ref r2, ref g2, ref b2));
    }

    [Fact]
    public void Luminance_limits_judge_brightness_as_it_looks()
    {
        // Saturated blue has HSL lightness 0.5 but luminance of only about 0.07.
        var byLightness = new ColourAdjustment { TargetHue = 240, NeutralProtection = 0, LightnessMax = 30, LightnessSmoothness = 0, Saturation = -100 };
        var byLuminance = byLightness.Clone();
        byLuminance.LimitBy = BrightnessMeasure.Luminance;

        float r = 0f, g = 0f, b = 1f;
        Assert.False(new AdjustmentSet([byLightness]).Adjust(ref r, ref g, ref b));
        Assert.True(new AdjustmentSet([byLuminance]).Adjust(ref r, ref g, ref b));

        Assert.Equal(0f, new AdjustmentSet([byLightness]).WeightOfColour(0f, 0f, 1f));
        Assert.Equal(1f, new AdjustmentSet([byLuminance]).WeightOfColour(0f, 0f, 1f));

        // Each adjustment in a set uses its own measure.
        var both = new AdjustmentSet([byLightness, byLuminance]);
        float r2 = 0f, g2 = 0f, b2 = 1f;
        Assert.True(both.Adjust(ref r2, ref g2, ref b2));
    }

    [Fact]
    public void Disabled_and_empty_adjustments_are_skipped()
    {
        Assert.True(new AdjustmentSet([new ColourAdjustment { Saturation = 50, Enabled = false }, new ColourAdjustment()]).IsEmpty);
        Assert.False(new AdjustmentSet([new ColourAdjustment()], includeAll: true).IsEmpty);
    }

    [Fact]
    public void Downsample_averages_blocks()
    {
        float[] p = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        var small = new RgbImage(4, 4, p, p, p).Downsample(2);
        Assert.Equal((2, 2), (small.Width, small.Height));
        Assert.Equal(new[] { 2.5f, 4.5f, 10.5f, 12.5f }, small.R);
    }
}

