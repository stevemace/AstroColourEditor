using AstroColour.Core;
using AstroColour.Core.Colour;
using AstroColour.Core.Stars;

namespace AstroColour.Tests;

public class StarLayerTests
{
    [Theory]
    [InlineData(0.2f, 0.5f, 1f)]
    [InlineData(0.6f, 0.9f, 0.5f)]
    [InlineData(0.0f, 1.0f, 0.25f)]
    public void Screen_matches_the_pixel_math(float starless, float stars, float x)
    {
        // ~((~starless) * (~(stars * x))), where ~v = 1 - v
        float expected = 1f - (1f - starless) * (1f - stars * x);
        Assert.Equal(expected, StarLayer.Screen(starless, stars, x), 6);
    }

    [Fact]
    public void Zero_brightness_or_dark_stars_leave_the_image_unchanged()
    {
        Assert.Equal(0.4f, StarLayer.Screen(0.4f, 0.8f, 0f), 6);
        Assert.Equal(0.4f, StarLayer.Screen(0.4f, 0f, 1f), 6);
        Assert.True(new StarLayer(new RgbImage(1, 1, [1f], [1f], [1f]), 0f).IsEmpty);
    }

    [Fact]
    public void Full_brightness_white_star_is_white() =>
        Assert.Equal(1f, StarLayer.Screen(0.3f, 1f, 1f), 6);

    [Fact]
    public void Preview_adds_stars_only_to_the_adjusted_view()
    {
        var image = new RgbImage(1, 1, [0.2f], [0.2f], [0.2f]);
        var stars = new StarLayer(new RgbImage(1, 1, [1f], [0.5f], [0f]), 1f);
        var bgra = new byte[4];

        ColourProcessor.RenderPreview(image, [], null, PreviewMode.Adjusted, null, bgra, stars: stars);
        Assert.Equal(255, bgra[2]);                                          // R: white star
        Assert.Equal((byte)(StarLayer.Screen(0.2f, 0.5f, 1f) * 255 + 0.5f), bgra[1]);
        Assert.Equal((byte)(0.2f * 255 + 0.5f), bgra[0]);                    // B: no star

        ColourProcessor.RenderPreview(image, [], null, PreviewMode.Original, null, bgra, stars: stars);
        Assert.Equal(bgra[0], bgra[2]);
    }

    [Fact]
    public void Apply_adds_the_stars_luminance_to_a_monochrome_image()
    {
        var image = new FakeImage(2, 1, [0.1f, 0.5f]);
        var stars = new StarLayer(new RgbImage(2, 1, [0f, 1f], [0f, 1f], [0f, 1f]), 0.5f);

        var planes = ColourProcessor.Apply(image, [], null, stars);

        Assert.Equal(0.1f, planes[0][0], 6);
        Assert.Equal(StarLayer.Screen(0.5f, 1f, 0.5f), planes[0][1], 6);
        Assert.Equal(0.5f, image.Planes[0][1]); // source untouched
    }

    [Fact]
    public void Stars_of_a_different_size_are_rejected()
    {
        var image = new FakeImage(2, 1, [0.1f, 0.5f]);
        var stars = new StarLayer(new RgbImage(1, 1, [1f], [1f], [1f]), 1f);
        Assert.Throws<ArgumentException>(() => ColourProcessor.Apply(image, [], null, stars));
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
