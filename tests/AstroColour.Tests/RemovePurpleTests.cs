using AstroColour.Core;
using AstroColour.Core.Colour;

namespace AstroColour.Tests;

public class RemovePurpleTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Moves_green_towards_the_red_blue_average(float x)
    {
        var image = new FakeImage(2, 1, [0.8f, 0.1f], [0.2f, 0.6f], [0.6f, 0.3f]);
        var planes = ColourProcessor.Apply(image, [], removePurple: x);

        for (int i = 0; i < 2; i++)
        {
            float r = image.Planes[0][i], g = image.Planes[1][i], b = image.Planes[2][i];
            Assert.Equal(g + x * ((r + b) / 2 - g), planes[1][i], 5);
            Assert.Equal(r, planes[0][i]);
            Assert.Equal(b, planes[2][i]);
        }
    }

    [Fact]
    public void Preview_ignores_it_in_original_mode()
    {
        var image = new RgbImage(1, 1, [0.8f], [0.2f], [0.6f]);
        var adjusted = new byte[4];
        var original = new byte[4];
        ColourProcessor.RenderPreview(image, [], null, PreviewMode.Adjusted, null, adjusted, removePurple: 1f);
        ColourProcessor.RenderPreview(image, [], null, PreviewMode.Original, null, original, removePurple: 1f);
        Assert.Equal(179, adjusted[1]); // BGRA: green becomes (0.8 + 0.6) / 2
        Assert.Equal(51, original[1]);
    }

    private sealed class FakeImage(int width, int height, params float[][] planes) : IAstroImage
    {
        public int Width => width;
        public int Height => height;
        public int Channels => planes.Length;
        public string ColorSpace => "RGB";
        public string SampleFormatName => "Float32";
        public float[][] Planes => planes;
        public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None) => throw new NotSupportedException();
    }
}
