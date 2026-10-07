using System.Runtime.InteropServices;
using AstroColour.Core;
using AstroColour.Core.Tiff;
using BitMiracle.LibTiff.Classic;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace AstroColour.Tests;

public class TiffFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tiff-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int W = 37, H = 23;

    private static double Fraction(int c, int i) => (c * W * H + i) / (double)(4 * W * H);

    /// <summary>Writes a TIFF whose samples are <paramref name="stored"/>(channel, pixel), using LibTiff directly.</summary>
    private string Write(int channels, int bits, SampleFormat format, Func<int, int, double> stored,
        Photometric photometric = Photometric.RGB, PlanarConfig planar = PlanarConfig.CONTIG,
        Compression compression = Compression.NONE, int predictor = 1, int tile = 0, string extension = ".tif",
        Action<LibTiff>? extraTags = null)
    {
        string path = Path.Combine(_dir, Guid.NewGuid() + extension);
        int bps = bits / 8;
        using var tif = LibTiff.Open(path, "w");
        tif.SetField(TiffTag.IMAGEWIDTH, W);
        tif.SetField(TiffTag.IMAGELENGTH, H);
        tif.SetField(TiffTag.SAMPLESPERPIXEL, channels);
        tif.SetField(TiffTag.BITSPERSAMPLE, bits);
        tif.SetField(TiffTag.SAMPLEFORMAT, format);
        tif.SetField(TiffTag.PHOTOMETRIC, photometric);
        tif.SetField(TiffTag.PLANARCONFIG, planar);
        tif.SetField(TiffTag.COMPRESSION, compression);
        if (predictor > 1) tif.SetField(TiffTag.PREDICTOR, predictor);
        if (channels == 4) tif.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
        extraTags?.Invoke(tif);

        bool separate = planar == PlanarConfig.SEPARATE;
        int spp = separate ? 1 : channels;
        byte[] Sample(int c, int i)
        {
            double v = stored(c, i);
            return (format, bits) switch
            {
                (SampleFormat.UINT, 8) => [(byte)v],
                (SampleFormat.UINT, 16) => BitConverter.GetBytes((ushort)v),
                (SampleFormat.INT, 16) => BitConverter.GetBytes((short)v),
                (SampleFormat.UINT, 32) => BitConverter.GetBytes((uint)v),
                (SampleFormat.IEEEFP, 16) => BitConverter.GetBytes((Half)v),
                (SampleFormat.IEEEFP, 32) => BitConverter.GetBytes((float)v),
                _ => BitConverter.GetBytes(v),
            };
        }

        if (tile == 0)
        {
            tif.SetField(TiffTag.ROWSPERSTRIP, 4);
            var line = new byte[W * spp * bps];
            for (int s = 0; s < (separate ? channels : 1); s++)
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                        for (int k = 0; k < spp; k++)
                            Sample(s + k, y * W + x).CopyTo(line, (x * spp + k) * bps);
                    Assert.True(tif.WriteScanline(line, y, (short)s));
                }
        }
        else
        {
            tif.SetField(TiffTag.TILEWIDTH, tile);
            tif.SetField(TiffTag.TILELENGTH, tile);
            var buffer = new byte[tile * tile * spp * bps];
            for (int s = 0; s < (separate ? channels : 1); s++)
                for (int ty = 0; ty < H; ty += tile)
                    for (int tx = 0; tx < W; tx += tile)
                    {
                        Array.Clear(buffer);
                        for (int y = 0; y < tile && ty + y < H; y++)
                            for (int x = 0; x < tile && tx + x < W; x++)
                                for (int k = 0; k < spp; k++)
                                    Sample(s + k, (ty + y) * W + tx + x).CopyTo(buffer, ((y * tile + x) * spp + k) * bps);
                        Assert.True(tif.WriteTile(buffer, tx, ty, 0, (short)s) > 0);
                    }
        }
        return path;
    }

    private string Output(string extension = ".tif") => Path.Combine(_dir, Guid.NewGuid() + extension);

    [Fact]
    public void ReadsUInt16RgbAndRoundTripsLzwWithMetadata()
    {
        byte[] icc = [1, 2, 3, 4, 5, 6, 7, 8];
        var path = Write(3, 16, SampleFormat.UINT, (c, i) => Math.Round(Fraction(c, i) * 65535),
            compression: Compression.LZW, predictor: 2, extraTags: tif =>
            {
                tif.SetField(TiffTag.IMAGEDESCRIPTION, "M42 widefield");
                tif.SetField(TiffTag.ICCPROFILE, icc.Length, icc);
                tif.SetField(TiffTag.XRESOLUTION, 300.0);
            });
        var f = TiffFile.Load(path);

        Assert.Equal((W, H, 3), (f.Width, f.Height, f.Channels));
        Assert.True(f.IsColour);
        Assert.Equal("RGB", f.ColorSpace);
        Assert.Equal("UInt16", f.SampleFormatName);
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < W * H; i++)
                Assert.Equal(Math.Round(Fraction(c, i) * 65535) / 65535, f.Planes[c][i], 1e-6);

        var edited = f.Planes.Select(p => p.Select(v => v / 2).ToArray()).ToArray();
        var output = Output();
        f.Save(output, edited);
        var g = TiffFile.Load(output);

        Assert.Equal(Compression.LZW, g.Compression);
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < W * H; i++)
                Assert.Equal(edited[c][i], g.Planes[c][i], 1 / 65535.0);

        using var tif = LibTiff.Open(output, "r");
        Assert.Equal("M42 widefield", tif.GetField(TiffTag.IMAGEDESCRIPTION)[0].ToString());
        Assert.Equal(icc, tif.GetField(TiffTag.ICCPROFILE)[1].GetBytes());
        Assert.Equal(300f, tif.GetField(TiffTag.XRESOLUTION)[0].ToFloat());
        Assert.Equal(2, tif.GetField(TiffTag.PREDICTOR)[0].ToInt());
    }

    [Fact]
    public void RoundTripsPlanarFloat32WithDeflateAndFloatingPointPredictor()
    {
        var path = Write(3, 32, SampleFormat.IEEEFP, Fraction,
            planar: PlanarConfig.SEPARATE, compression: Compression.ADOBE_DEFLATE, predictor: 3);
        var f = TiffFile.Load(path);

        Assert.Equal("Float32", f.SampleFormatName);
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < W * H; i++)
                Assert.Equal((float)Fraction(c, i), f.Planes[c][i]);

        var output = Output();
        f.Save(output);
        var g = TiffFile.Load(output);
        Assert.Equal(Compression.ADOBE_DEFLATE, g.Compression);
        for (int c = 0; c < 3; c++)
            Assert.Equal(f.Planes[c], g.Planes[c]);

        using var tif = LibTiff.Open(output, "r");
        Assert.Equal(PlanarConfig.SEPARATE, (PlanarConfig)tif.GetField(TiffTag.PLANARCONFIG)[0].ToInt());
    }

    [Theory]
    [InlineData(8, SampleFormat.UINT, 255.0, "UInt8")]
    [InlineData(16, SampleFormat.INT, 32767.0, "Int16")]
    [InlineData(32, SampleFormat.UINT, 4294967295.0, "UInt32")]
    public void ReadsTiledGreyscaleIntegersByTheirTypeRange(int bits, SampleFormat format, double max, string name)
    {
        var path = Write(1, bits, format, (_, i) => Math.Round(i / (double)(W * H) * max),
            photometric: Photometric.MINISBLACK, tile: 16);
        var f = TiffFile.Load(path);

        Assert.Equal(name, f.SampleFormatName);
        Assert.False(f.IsColour);
        Assert.Equal("Gray", f.ColorSpace);
        for (int i = 0; i < W * H; i++)
            Assert.Equal(Math.Round(i / (double)(W * H) * max) / max, f.Planes[0][i], 1e-6);

        var output = Output();
        f.Save(output);
        Assert.Equal(f.Planes[0], TiffFile.Load(output).Planes[0]);
    }

    [Fact]
    public void ScalesFloatsInThe65535Range()
    {
        var path = Write(1, 32, SampleFormat.IEEEFP, (_, i) => i * 50.0, photometric: Photometric.MINISBLACK);
        var f = TiffFile.Load(path);
        Assert.Equal(34 * 50 / 65535f, f.Planes[0][34], 6);

        var output = Output();
        f.Save(output);
        using var tif = LibTiff.Open(output, "r");
        var line = new byte[tif.ScanlineSize()];
        tif.ReadScanline(line, 0);
        Assert.Equal(34 * 50f, MemoryMarshal.Cast<byte, float>(line)[34], 2);
    }

    [Fact]
    public void ReadsHalfFloats()
    {
        var path = Write(3, 16, SampleFormat.IEEEFP, (c, i) => (float)(Half)Fraction(c, i));
        var f = TiffFile.Load(path);
        Assert.Equal("Float16", f.SampleFormatName);
        Assert.Equal((float)(Half)Fraction(2, 10), f.Planes[2][10]);
    }

    [Fact]
    public void InvertsMinIsWhiteImagesBothWays()
    {
        var path = Write(1, 8, SampleFormat.UINT, (_, i) => i % 256, photometric: Photometric.MINISWHITE);
        var f = TiffFile.Load(path);
        Assert.Equal(1f, f.Planes[0][0]);
        Assert.Equal(1 - 100 / 255f, f.Planes[0][100], 6);

        var output = Output();
        f.Save(output);
        using var tif = LibTiff.Open(output, "r");
        Assert.Equal(Photometric.MINISWHITE, (Photometric)tif.GetField(TiffTag.PHOTOMETRIC)[0].ToInt());
        var line = new byte[tif.ScanlineSize()];
        tif.ReadScanline(line, 0);
        Assert.Equal(10, line[10]);
    }

    [Fact]
    public void KeepsAlphaAsAnExtraChannel()
    {
        var path = Write(4, 8, SampleFormat.UINT, (c, i) => c == 3 ? 200 : Math.Round(Fraction(c, i) * 255));
        var f = TiffFile.Load(path);
        Assert.Equal(4, f.Channels);
        Assert.True(f.IsColour);

        var output = Output();
        f.Save(output);
        var g = TiffFile.Load(output);
        Assert.Equal(4, g.Channels);
        Assert.All(g.Planes[3], v => Assert.Equal(200 / 255f, v, 6));
    }

    [Fact]
    public void GreyscaleWithAlphaIsNotColour()
    {
        var path = Write(2, 8, SampleFormat.UINT, (_, i) => i % 256, photometric: Photometric.MINISBLACK);
        Assert.False(TiffFile.Load(path).IsColour);
    }

    [Fact]
    public void RejectsPaletteImages()
    {
        var path = Write(1, 8, SampleFormat.UINT, (_, i) => i % 256, photometric: Photometric.PALETTE, extraTags: tif =>
        {
            var map = new short[256];
            tif.SetField(TiffTag.COLORMAP, map, map, map);
        });
        Assert.Throws<NotSupportedException>(() => TiffFile.Load(path));
    }

    [Fact]
    public void RejectsFilesThatAreNotTiff()
    {
        string path = Output();
        File.WriteAllText(path, "not a tiff");
        Assert.ThrowsAny<Exception>(() => TiffFile.Load(path));
    }

    [Fact]
    public void AstroImageFileLoadsTiffByExtension()
    {
        var path = Write(3, 16, SampleFormat.UINT, (c, i) => i, extension: ".TIFF");
        Assert.IsType<TiffFile>(AstroImageFile.Load(path));
        var (name, extensions) = AstroImageFile.SaveFormat(path);
        Assert.Equal("TIFF image", name);
        Assert.Equal([".tiff", ".tif"], extensions);
    }
}
