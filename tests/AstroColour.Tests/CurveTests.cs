using AstroColour.Core;
using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Tests;

public class CurveTests
{
    [Fact]
    public void Default_curve_is_identity()
    {
        var curve = new ToneCurve();
        Assert.True(curve.IsIdentity);
        foreach (float x in new[] { 0f, 0.1f, 0.5f, 0.93f, 1f })
            Assert.Equal(x, curve.Evaluate(x), 5);
        Assert.True(new CurveSet().IsIdentity);
    }

    [Fact]
    public void Curve_passes_through_points_and_is_monotone()
    {
        var curve = new ToneCurve([new(0f, 0f), new(0.2f, 0.5f), new(0.25f, 0.55f), new(1f, 1f)]);
        Assert.False(curve.IsIdentity);
        Assert.Equal(0.5f, curve.Evaluate(0.2f), 5);
        Assert.Equal(0.55f, curve.Evaluate(0.25f), 5);

        // A sharp bend must not overshoot or turn back.
        var values = curve.Sample(1001);
        for (int i = 1; i < values.Length; i++)
            Assert.True(values[i] >= values[i - 1], $"not monotone at {i}");
        Assert.All(values, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void End_points_act_as_black_and_white_points()
    {
        var curve = new ToneCurve([new(0.1f, 0f), new(0.9f, 1f)]);
        Assert.Equal(0f, curve.Evaluate(0.05f));
        Assert.Equal(0.5f, curve.Evaluate(0.5f), 4);
        Assert.Equal(1f, curve.Evaluate(0.95f));
    }

    [Fact]
    public void Channel_curve_is_applied_before_composite()
    {
        var curves = new CurveSet();
        curves[CurveChannel.Red].Points[1] = new(1f, 0.5f);   // red halved
        curves[CurveChannel.Rgb].Points[0] = new(0f, 0.2f);    // composite lifts blacks: y = 0.2 + 0.8x
        var lookup = new CurveLookup(curves);

        float r = 0.8f, g = 0.5f, b = 0.5f;
        lookup.Apply(ref r, ref g, ref b);
        Assert.Equal(0.2f + 0.8f * 0.4f, r, 3);
        Assert.Equal(0.6f, g, 3);
        Assert.Equal(0.6f, b, 3);
    }

    [Fact]
    public void Identity_channels_keep_out_of_range_values()
    {
        var curves = new CurveSet();
        curves[CurveChannel.Green].Points.Insert(1, new(0.5f, 0.7f));
        var lookup = new CurveLookup(curves);

        float r = 1.5f, g = 1.5f, b = -0.1f;
        lookup.Apply(ref r, ref g, ref b);
        Assert.Equal((1.5f, 1f, -0.1f), (r, g, b));
        Assert.True(new CurveLookup(new CurveSet()).IsEmpty);
    }

    [Fact]
    public void Apply_uses_only_the_composite_curve_for_mono_images()
    {
        var curves = new CurveSet();
        curves[CurveChannel.Rgb].Points[1] = new(1f, 0.5f);
        curves[CurveChannel.Red].Points[1] = new(1f, 0f);

        var mono = new TestImage(2, 1, [[0.2f, 0.8f]]);
        var planes = ColourProcessor.Apply(mono, [], curves);
        Assert.Equal(0.1f, planes[0][0], 3);
        Assert.Equal(0.4f, planes[0][1], 3);
        Assert.Equal(0.2f, mono.Planes[0][0]); // source untouched

        var colour = new TestImage(1, 1, [[0.8f], [0.8f], [0.8f]]);
        planes = ColourProcessor.Apply(colour, [], curves);
        Assert.Equal(0f, planes[0][0], 4);
        Assert.Equal(0.4f, planes[1][0], 4);
        Assert.Equal(0.4f, planes[2][0], 4);
    }

    [Fact]
    public void Histogram_is_normalised_per_channel()
    {
        float[] r = [0f, 0.5f, 0.5f, 1f], g = [0.25f, 0.25f, 0.25f, 0.25f], b = [0f, 0f, 0f, 0f];
        var histogram = CurveSet.Histogram(new RgbImage(2, 2, r, g, b), bins: 4);
        Assert.Equal(4, histogram.Length);
        float[] expected = [MathF.Sqrt(0.5f), 0f, 1f, MathF.Sqrt(0.5f)]; // square-root scaled to the tallest interior bin
        for (int i = 0; i < 4; i++) Assert.Equal(expected[i], histogram[(int)CurveChannel.Red][i], 4);
        Assert.Equal(1f, histogram[(int)CurveChannel.Green][1]);
        Assert.Equal(1f, histogram[(int)CurveChannel.Blue][0]); // clipped bins are capped at full height
    }

    private sealed class TestImage(int width, int height, float[][] planes) : IAstroImage
    {
        public int Width => width;
        public int Height => height;
        public int Channels => planes.Length;
        public string ColorSpace => Channels >= 3 ? "RGB" : "Gray";
        public string SampleFormatName => "Float32";
        public float[][] Planes => planes;
        public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None, CropRect? crop = null) => throw new NotSupportedException();
    }
}
