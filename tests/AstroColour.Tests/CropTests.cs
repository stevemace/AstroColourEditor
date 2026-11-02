using System.Globalization;
using AstroColour.Core;
using AstroColour.Core.Fits;
using AstroColour.Core.Tiff;
using AstroColour.Core.Xisf;

namespace AstroColour.Tests;

public class CropTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crop-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int W = 9, H = 6;

    private static float Value(int c, int i) => (c * W * H + i) / (float)(3 * W * H);

    private static float[][] Planes(int channels) =>
        Enumerable.Range(0, channels).Select(c => Enumerable.Range(0, W * H).Select(i => Value(c, i)).ToArray()).ToArray();

    private static readonly FitsKeyword[] Keywords =
    [
        new("OBJECT", "'M42'", "target"),
        new("CTYPE1", "'RA---TAN'", ""),
        new("CRPIX1", "4.5", "reference pixel x"),
        new("CRPIX2", "3.25", "reference pixel y"),
        new("CRVAL1", "83.82", ""),
        new("CD1_1", "-0.0003", ""),
    ];

    private static readonly CropRect Crop = new(2, 1, 5, 3); // keeps columns 2–6 and rows 1–3

    private string Output(string extension) => Path.Combine(_dir, Guid.NewGuid() + extension);

    private static double Number(IAstroImage image, string name) =>
        double.Parse(image.FitsKeywords.Single(k => k.Name == name).Value, CultureInfo.InvariantCulture);

    private static void AssertCroppedPixels(IAstroImage image, int channels, float tolerance = 1e-6f)
    {
        Assert.Equal((Crop.Width, Crop.Height), (image.Width, image.Height));
        for (int c = 0; c < channels; c++)
            for (int y = 0; y < Crop.Height; y++)
                for (int x = 0; x < Crop.Width; x++)
                    Assert.Equal(Value(c, (Crop.Y + y) * W + Crop.X + x), image.Planes[c][y * Crop.Width + x], tolerance);
    }

    // ------------------------------------------------------------- geometry

    [Fact]
    public void Copies_the_rectangle()
    {
        var cropped = Crop.Apply(Planes(1), W)[0];
        Assert.Equal(Crop.Width * Crop.Height, cropped.Length);
        Assert.Equal(Value(0, 1 * W + 2), cropped[0]);
        Assert.Equal(Value(0, 3 * W + 6), cropped[^1]);
    }

    [Theory]
    [InlineData(Rotation.None)]
    [InlineData(Rotation.Clockwise)]
    [InlineData(Rotation.HalfTurn)]
    [InlineData(Rotation.Anticlockwise)]
    public void Rotating_the_rectangle_matches_rotating_the_pixels(Rotation rotation)
    {
        // Cropping then turning gives the same pixels as turning then cropping the turned rectangle.
        var planes = Planes(1);
        var (tw, th) = rotation.Size(W, H);
        var turnedCrop = Crop.Rotate(rotation, W, H);
        Assert.True(turnedCrop.FitsIn(tw, th));
        var a = rotation.Apply(Crop.Apply(planes, W), Crop.Width, Crop.Height)[0];
        var b = turnedCrop.Apply(rotation.Apply(planes, W, H), tw)[0];
        Assert.Equal(a, b);
        Assert.Equal(Crop, turnedCrop.Unrotate(rotation, W, H));
    }

    [Fact]
    public void A_crop_of_the_whole_image_is_none()
    {
        Assert.Null(CropRect.Effective(new CropRect(0, 0, W, H), W, H));
        Assert.Null(CropRect.Effective(null, W, H));
        Assert.Equal(Crop, CropRect.Effective(Crop, W, H));
        Assert.Throws<ArgumentOutOfRangeException>(() => CropRect.Effective(new CropRect(5, 0, 5, 2), W, H));
    }

    // ------------------------------------------------------------------ WCS

    [Theory]
    [InlineData(false, 2, 1)]
    [InlineData(true, 2, 2)] // bottom-up: 6 rows, crop ends at row 4, so 2 rows below it are cut
    public void Crop_shift_counts_rows_from_the_first_row_stored(bool firstRowAtBottom, int x, int y) =>
        Assert.Equal((x, y), Wcs.CropShift(Crop, H, firstRowAtBottom));

    [Theory]
    [InlineData("CRPIX1", 1)]
    [InlineData("CRPIX2", 2)]
    [InlineData("crpix2a", 2)]
    [InlineData("CRPIX3", 0)]
    [InlineData("CRVAL1", 0)]
    public void Recognises_reference_pixel_keywords(string name, int axis) => Assert.Equal(axis, Wcs.CrpixAxis(name));

    [Fact]
    public void Moves_reference_pixel_values()
    {
        Assert.Equal("2.5", Wcs.ShiftValue("4.5", 2));
        Assert.Equal("1000.5", Wcs.ShiftValue("1.2345E+03", 234));
        Assert.Equal("-1.0", Wcs.ShiftValue("1D0", 2));
        Assert.Null(Wcs.ShiftValue("'abc'", 1));
        Assert.Equal(new FitsKeyword("CTYPE1", "'RA'", ""), Wcs.Crop(new FitsKeyword("CTYPE1", "'RA'", ""), (2, 1)));
        Assert.Equal(new FitsKeyword("CRPIX2", "2.25", "y"), Wcs.Crop(new FitsKeyword("CRPIX2", "3.25", "y"), (2, 1)));
    }

    // -------------------------------------------------------------- formats

    [Fact]
    public void Fits_keeps_the_wcs_with_the_reference_pixel_moved()
    {
        string source = Output(".fits");
        FitsFile.Create(W, H, Planes(3), -32, Keywords).Save(source);
        var image = FitsFile.Load(source);
        Assert.False(image.BottomUp);

        string output = Output(".fits");
        image.Save(output, crop: Crop);
        var saved = FitsFile.Load(output);

        AssertCroppedPixels(saved, 3);
        Assert.Equal(4.5 - 2, Number(saved, "CRPIX1"));
        Assert.Equal(3.25 - 1, Number(saved, "CRPIX2"));
        Assert.Equal(83.82, Number(saved, "CRVAL1"));
        Assert.Equal(-0.0003, Number(saved, "CD1_1"));
        Assert.Equal("reference pixel x", saved.FitsKeywords.Single(k => k.Name == "CRPIX1").Comment);
        Assert.Equal(Crop.Width, (int)Number(saved, "NAXIS1"));
        Assert.Equal(Crop.Height, (int)Number(saved, "NAXIS2"));
    }

    [Fact]
    public void Xisf_keeps_the_wcs_keywords_counted_from_the_bottom_and_drops_pixinsight_solution()
    {
        string source = Output(".xisf");
        XisfFile.Create(W, H, Planes(3), XisfSampleFormat.Float32, Keywords).Save(source);
        var image = XisfFile.Load(source);

        string output = Output(".xisf");
        image.Save(output, crop: Crop);
        var saved = XisfFile.Load(output);

        AssertCroppedPixels(saved, 3);
        Assert.Equal(4.5 - 2, Number(saved, "CRPIX1"));
        Assert.Equal(3.25 - (H - Crop.Bottom), Number(saved, "CRPIX2"));
        Assert.Contains(saved.FitsKeywords, k => k.Name == "OBJECT");
    }

    [Fact]
    public void Cropping_and_rotating_drops_the_wcs()
    {
        string source = Output(".fits");
        FitsFile.Create(W, H, Planes(1), -32, Keywords).Save(source);
        string output = Output(".fits");
        FitsFile.Load(source).Save(output, rotation: Rotation.Clockwise, crop: Crop);

        var saved = FitsFile.Load(output);
        Assert.Equal((Crop.Height, Crop.Width), (saved.Width, saved.Height));
        Assert.DoesNotContain(saved.FitsKeywords, k => k.Name.StartsWith("CRPIX"));
        Assert.Equal(Rotation.Clockwise.Apply(Crop.Apply(Planes(1), W), Crop.Width, Crop.Height)[0], saved.Planes[0]);
    }

    [Fact]
    public void Converting_moves_the_reference_pixel_by_the_source_convention()
    {
        string source = Output(".fits");
        FitsFile.Create(W, H, Planes(3), 16, Keywords).Save(source);
        string output = Output(".xisf");
        AstroImageFile.Save(AstroImageFile.Load(source), output, crop: Crop);

        var saved = AstroImageFile.Load(output);
        AssertCroppedPixels(saved, 3, tolerance: 1e-4f);
        Assert.Equal(4.5 - 2, Number(saved, "CRPIX1"));
        // The top-down FITS source counts rows from the top, so the crop's top row moves CRPIX2 by 1; the XISF
        // output counts from the bottom, so the result is then flipped within the 3 rows kept.
        Assert.Equal(Crop.Height + 1 - (3.25 - 1), Number(saved, "CRPIX2"));
    }

    [Theory]
    [InlineData(".tif")]
    [InlineData(".png")]
    [InlineData(".jpg")]
    public void Other_formats_are_cropped_too(string extension)
    {
        string source = Output(".tif");
        TiffFile.Create(W, H, Planes(3), 16, isFloat: false).Save(source);
        string output = Output(extension);
        AstroImageFile.Save(AstroImageFile.Load(source), output, crop: Crop);
        if (extension == ".tif") AssertCroppedPixels(AstroImageFile.Load(output), 3, tolerance: 1e-4f);
        else Assert.True(new FileInfo(output).Length > 0);
    }
}
