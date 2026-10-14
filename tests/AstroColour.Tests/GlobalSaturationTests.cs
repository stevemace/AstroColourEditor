using AstroColour.Core;
using AstroColour.Core.Colour;
using AstroColour.Core.Curves;

namespace AstroColour.Tests;

public class GlobalSaturationTests
{
    [Fact]
    public void Desaturates_every_colour_after_the_curves()
    {
        var image = new FakeImage(2, 1, [0.8f, 0.2f], [0.2f, 0.3f], [0.2f, 0.9f]);
        var curves = new CurveSet();
        curves[CurveChannel.Red].Points[1] = new CurvePoint(1f, 0.5f); // halve red: (0.4, 0.2, 0.2)
        var planes = ColourProcessor.Apply(image, [], curves, saturation: -100);

        Assert.Equal(0.3f, planes[0][0], 4); // grey at the curved colour's lightness
        Assert.Equal(0.3f, planes[1][0], 4);
        Assert.Equal(planes[0][1], planes[2][1], 4); // the blue pixel is greyed too
    }

    [Fact]
    public void Leaves_neutrals_and_hue_alone()
    {
        var image = new FakeImage(2, 1, [0.4f, 0.6f], [0.4f, 0.3f], [0.4f, 0.2f]);
        var planes = ColourProcessor.Apply(image, [], saturation: 50);

        Assert.Equal((0.4f, 0.4f, 0.4f), (planes[0][0], planes[1][0], planes[2][0]));
        Assert.Equal(ColourMath.Hue(0.6f, 0.3f, 0.2f), ColourMath.Hue(planes[0][1], planes[1][1], planes[2][1]), 2);
        Assert.True(planes[0][1] - planes[2][1] > 0.6f - 0.2f);
    }

    [Fact]
    public void Preview_ignores_saturation_in_original_mode()
    {
        var image = new RgbImage(1, 1, [0.8f], [0.2f], [0.2f]);
        var adjusted = new byte[4];
        var original = new byte[4];
        ColourProcessor.RenderPreview(image, [], null, PreviewMode.Adjusted, null, adjusted, saturation: -100);
        ColourProcessor.RenderPreview(image, [], null, PreviewMode.Original, null, original, saturation: -100);
        Assert.Equal(adjusted[0], adjusted[2]);
        Assert.Equal(204, original[2]);
    }

    private sealed class FakeImage(int width, int height, params float[][] planes) : IAstroImage
    {
        public int Width => width;
        public int Height => height;
        public int Channels => planes.Length;
        public string ColorSpace => planes.Length >= 3 ? "RGB" : "Gray";
        public string SampleFormatName => "Float32";
        public float[][] Planes => planes;
        public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None) => throw new NotSupportedException();
    }
}
