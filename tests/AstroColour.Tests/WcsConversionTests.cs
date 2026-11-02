using System.Globalization;
using AstroColour.Core;
using AstroColour.Core.Fits;
using AstroColour.Core.Xisf;

namespace AstroColour.Tests;

/// <summary>
/// A plate solution carried between FITS (written top row first) and XISF (whose keywords PixInsight counts from the
/// bottom row) must still put every displayed pixel at the same place on the sky.
/// </summary>
public class WcsConversionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wcs-conversion-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int W = 8, H = 5;

    private static float[][] Planes() => [Enumerable.Range(0, W * H).Select(i => i / (float)(W * H)).ToArray()];

    private static readonly FitsKeyword[] CdSolution =
    [
        new("OBJECT", "'M42'", ""),
        new("CTYPE1", "'RA---TAN-SIP'", ""),
        new("CTYPE2", "'DEC--TAN-SIP'", ""),
        new("CRPIX1", "3.5", ""),
        new("CRPIX2", "2.25", ""),
        new("CRVAL1", "83.82", ""),
        new("CRVAL2", "-5.39", ""),
        new("CD1_1", "-0.0003", ""),
        new("CD1_2", "0.00005", ""),
        new("CD2_1", "0.00004", ""),
        new("CD2_2", "0.0003", ""),
        new("A_ORDER", "2", ""),
        new("A_0_2", "1E-6", ""),
        new("A_1_1", "2E-6", ""),
        new("A_2_0", "3E-6", ""),
        new("B_ORDER", "2", ""),
        new("B_0_2", "4E-6", ""),
        new("B_1_1", "5E-6", ""),
        new("B_2_0", "6E-6", ""),
    ];

    private string Output(string extension) => Path.Combine(_dir, Guid.NewGuid() + extension);

    private static double Get(IReadOnlyList<FitsKeyword> keywords, string name, double missing = 0) =>
        keywords.FirstOrDefault(k => k.Name == name) is { } k ? double.Parse(k.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : missing;

    /// <summary>
    /// Intermediate world coordinates (degrees, before the sky projection) of the displayed pixel at
    /// (<paramref name="col"/>, <paramref name="row"/>), with SIP distortion, as a reader would compute them.
    /// </summary>
    private static (double, double) Sky(IReadOnlyList<FitsKeyword> k, bool firstRowAtBottom, int height, int col, int row)
    {
        double x = col + 1, y = firstRowAtBottom ? height - row : row + 1;
        double u = x - Get(k, "CRPIX1"), v = y - Get(k, "CRPIX2");
        double du = 0, dv = 0;
        for (int p = 0; p <= 2; p++)
            for (int q = 0; p + q <= 2; q++)
            {
                du += Get(k, $"A_{p}_{q}") * Math.Pow(u, p) * Math.Pow(v, q);
                dv += Get(k, $"B_{p}_{q}") * Math.Pow(u, p) * Math.Pow(v, q);
            }
        u += du;
        v += dv;
        return (Get(k, "CD1_1") * u + Get(k, "CD1_2") * v, Get(k, "CD2_1") * u + Get(k, "CD2_2") * v);
    }

    private static void AssertSameSky(IReadOnlyList<FitsKeyword> before, bool beforeBottom, IReadOnlyList<FitsKeyword> after, bool afterBottom)
    {
        foreach (var (col, row) in new[] { (0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1), (3, 2) })
        {
            var (a1, a2) = Sky(before, beforeBottom, H, col, row);
            var (b1, b2) = Sky(after, afterBottom, H, col, row);
            Assert.Equal(a1, b1, 12);
            Assert.Equal(a2, b2, 12);
        }
    }

    [Fact]
    public void Fits_to_xisf_keeps_every_pixel_on_the_same_sky_position()
    {
        string source = Output(".fits");
        FitsFile.Create(W, H, Planes(), -32, CdSolution).Save(source);
        var fits = AstroImageFile.Load(source);
        string output = Output(".xisf");
        AstroImageFile.Save(fits, output);

        var xisf = AstroImageFile.Load(output);
        Assert.True(xisf.WcsFirstRowAtBottom);
        Assert.Equal(H + 1 - 2.25, Get(xisf.FitsKeywords, "CRPIX2"));
        AssertSameSky(fits.FitsKeywords, false, xisf.FitsKeywords, true);
    }

    [Fact]
    public void Xisf_to_fits_keeps_every_pixel_on_the_same_sky_position()
    {
        string source = Output(".xisf");
        XisfFile.Create(W, H, Planes(), XisfSampleFormat.Float32, CdSolution).Save(source);
        var xisf = AstroImageFile.Load(source);
        string output = Output(".fits");
        AstroImageFile.Save(xisf, output);

        var fits = AstroImageFile.Load(output);
        Assert.False(fits.WcsFirstRowAtBottom);
        AssertSameSky(xisf.FitsKeywords, true, fits.FitsKeywords, false);
        Assert.Contains(fits.FitsKeywords, k => k.Name == "OBJECT");
    }

    [Fact]
    public void Round_trip_restores_the_original_keywords()
    {
        var flipped = Wcs.FlipRows(Wcs.FlipRows(CdSolution, H), H);
        foreach (var k in CdSolution)
            if (double.TryParse(k.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                Assert.Equal(v, Get(flipped, k.Name), 12);
    }

    [Fact]
    public void Cdelt_solutions_flip_cdelt2()
    {
        FitsKeyword[] keywords = [new("CRPIX2", "2", ""), new("CDELT1", "-0.0003", ""), new("CDELT2", "0.0003", ""), new("CROTA2", "12.5", "")];
        var flipped = Wcs.FlipRows(keywords, H);
        Assert.Equal(H + 1 - 2, Get(flipped, "CRPIX2"));
        Assert.Equal(-0.0003, Get(flipped, "CDELT1"));
        Assert.Equal(-0.0003, Get(flipped, "CDELT2"));
        Assert.Equal(12.5, Get(flipped, "CROTA2"));
    }

    [Fact]
    public void Pc_solutions_flip_the_matrix_not_cdelt2()
    {
        FitsKeyword[] keywords = [new("CDELT2", "0.0003", ""), new("PC1_2", "0.1", ""), new("PC2_2", "0.9", ""), new("PC2_1", "0.2", "")];
        var flipped = Wcs.FlipRows(keywords, H);
        Assert.Equal(0.0003, Get(flipped, "CDELT2"));
        Assert.Equal(-0.1, Get(flipped, "PC1_2"));
        Assert.Equal(-0.9, Get(flipped, "PC2_2"));
        Assert.Equal(0.2, Get(flipped, "PC2_1"));
    }

    [Fact]
    public void Pv_distortion_drops_the_solution_rather_than_leaving_it_wrong()
    {
        FitsKeyword[] keywords = [new("OBJECT", "'M42'", ""), new("CRPIX2", "2", ""), new("CD2_2", "0.0003", ""), new("PV1_1", "1", "")];
        var flipped = Wcs.FlipRows(keywords, H);
        Assert.Equal(["OBJECT"], flipped.Select(k => k.Name));
    }

    [Fact]
    public void Formats_without_keywords_or_rotation_are_unaffected()
    {
        // Rotating drops the solution outright, so there is nothing to flip.
        string source = Output(".fits");
        FitsFile.Create(W, H, Planes(), -32, CdSolution).Save(source);
        string output = Output(".xisf");
        AstroImageFile.Save(AstroImageFile.Load(source), output, rotation: Rotation.Clockwise);
        Assert.DoesNotContain(AstroImageFile.Load(output).FitsKeywords, k => k.Name.StartsWith("CRPIX"));
    }
}
