using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Tests;

public class HueCurveTests
{
    [Fact]
    public void New_curve_is_flat_with_six_points()
    {
        var curve = new HueCurve();
        Assert.True(curve.IsIdentity);
        Assert.Equal(HueCurve.DefaultPointCount, curve.Points.Count);
        Assert.All(curve.Sample(50), v => Assert.Equal(HueCurve.Neutral, v, 5));
        Assert.True(new HueCurveLookup(new HueCurveSet()).IsEmpty);
    }

    [Fact]
    public void Curve_wraps_round_the_hue_circle()
    {
        // A single raised point at red (0°) lifts hues either side of 0°/360° alike.
        var curve = new HueCurve([new(0f, 0.5f), new(1 / 6f, 0.5f), new(5 / 6f, 0.5f)]);
        curve.Points[0] = new CurvePoint(0f, 0.8f);
        Assert.Equal(0.8f, curve.Evaluate(0f), 4);
        Assert.Equal(0.8f, curve.Evaluate(1f), 4);
        Assert.Equal(curve.Evaluate(0.05f), curve.Evaluate(0.95f), 4);
        Assert.InRange(curve.Evaluate(0.05f), 0.5001f, 0.7999f);
        Assert.Equal(0.5f, curve.Evaluate(0.5f), 4);
    }

    [Fact]
    public void Curve_does_not_overshoot()
    {
        var curve = new HueCurve();
        curve.Points[2] = curve.Points[2] with { Y = 1f };
        Assert.All(curve.Sample(361), v => Assert.InRange(v, 0.5f - 1e-5f, 1f));
    }

    [Fact]
    public void No_points_or_one_point_is_flat()
    {
        var empty = new HueCurve([]);
        Assert.True(empty.IsIdentity);
        Assert.Equal(0.5f, empty.Evaluate(0.3f));
        var single = new HueCurve([new(0.4f, 0.7f)]);
        Assert.Equal(0.7f, single.Evaluate(0.1f), 4);
        Assert.Equal(0.7f, single.Evaluate(0.9f), 4);
    }

    [Fact]
    public void Hue_versus_hue_shifts_only_the_chosen_hue()
    {
        var curves = new HueCurveSet();
        // Green (120°) is point 2: raise it to shift green by +45°.
        curves.Hue.Points[2] = curves.Hue.Points[2] with { Y = 0.75f };
        Assert.Equal(45f, HueCurveSet.HueShiftOf(curves.Hue.Evaluate(1 / 3f)), 3);
        var lookup = new HueCurveLookup(curves);

        float r = 0.2f, g = 0.8f, b = 0.2f; // pure green hue
        ColourMath.RgbToHsl(r, g, b, out _, out float s0, out float l0, out _);
        lookup.Apply(ref r, ref g, ref b);
        ColourMath.RgbToHsl(r, g, b, out float h, out float s, out float l, out _);
        Assert.Equal(165f, h, 1);
        Assert.Equal(s0, s, 3);
        Assert.Equal(l0, l, 3);

        float rr = 0.8f, rg = 0.2f, rb = 0.2f; // red is untouched
        lookup.Apply(ref rr, ref rg, ref rb);
        Assert.Equal(0.8f, rr, 4);
        Assert.Equal(0.2f, rg, 4);
        Assert.Equal(0.2f, rb, 4);
    }

    [Fact]
    public void Hue_versus_saturation_mutes_the_chosen_hue()
    {
        var curves = new HueCurveSet();
        curves.Saturation.Points[4] = curves.Saturation.Points[4] with { Y = 0f }; // blue (240°) to grey
        var lookup = new HueCurveLookup(curves);

        float r = 0.2f, g = 0.2f, b = 0.9f; // hue 240°
        ColourMath.RgbToHsl(r, g, b, out _, out _, out float l0, out _);
        lookup.Apply(ref r, ref g, ref b);
        Assert.Equal(r, g, 4);
        Assert.Equal(g, b, 4);
        Assert.Equal(l0, r, 3);
    }

    [Fact]
    public void Neutral_pixels_are_untouched()
    {
        var curves = new HueCurveSet();
        for (int i = 0; i < curves.Hue.Points.Count; i++) curves.Hue.Points[i] = curves.Hue.Points[i] with { Y = 1f };
        var lookup = new HueCurveLookup(curves);
        float r = 0.4f, g = 0.4f, b = 0.4f;
        lookup.Apply(ref r, ref g, ref b);
        Assert.Equal((0.4f, 0.4f, 0.4f), (r, g, b));
    }

    [Fact]
    public void Hue_curves_apply_last_among_the_base_adjustments()
    {
        var curves = new HueCurveSet();
        curves.Saturation.Points[0] = curves.Saturation.Points[0] with { Y = 0f }; // red to grey
        var adjustments = BaseAdjustments.Create(null, saturation: 50f, hueCurves: new HueCurveLookup(curves));
        Assert.NotNull(adjustments);
        float r = 0.8f, g = 0.2f, b = 0.2f;
        adjustments.Apply(ref r, ref g, ref b);
        Assert.Equal(r, g, 4); // the saturation boost is undone by the hue curve that follows it
        Assert.Null(BaseAdjustments.Create(null, hueCurves: new HueCurveLookup(new HueCurveSet())));
    }

    [Fact]
    public void Clone_and_copy_are_independent()
    {
        var curves = new HueCurveSet();
        var copy = curves.Clone();
        copy.Hue.Points[0] = copy.Hue.Points[0] with { Y = 0.9f };
        Assert.True(curves.IsIdentity);
        curves.CopyFrom(copy);
        Assert.False(curves.IsIdentity);
        copy.Reset();
        Assert.False(curves.IsIdentity);
    }

    [Fact]
    public void Histogram_peaks_at_the_image_hue()
    {
        var image = new RgbImage(4, 1, [0.9f, 0.9f, 0.5f, 0.5f], [0.1f, 0.1f, 0.5f, 0.5f], [0.1f, 0.1f, 0.5f, 0.5f]);
        var histogram = HueCurveSet.Histogram(image, bins: 36);
        Assert.Equal(1f, histogram[0]);
        Assert.All(histogram.Skip(1), v => Assert.Equal(0f, v));
    }
}
