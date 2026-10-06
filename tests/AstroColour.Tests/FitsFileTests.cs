using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AstroColour.Core;
using AstroColour.Core.Fits;

namespace AstroColour.Tests;

public class FitsFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fits-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int W = 7, H = 5;

    private static string Card(string key, object value, string comment = "")
    {
        string v = value switch
        {
            string s => $"'{s.PadRight(8)}'".PadRight(20),
            bool b => (b ? "T" : "F").PadLeft(20),
            double d => d.ToString("R", CultureInfo.InvariantCulture).PadLeft(20),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!.PadLeft(20),
        };
        string card = $"{key,-8}= {v}" + (comment.Length > 0 ? $" / {comment}" : "");
        return card.PadRight(80);
    }

    private static byte[] Hdu(IEnumerable<string> cards, byte[] data)
    {
        var header = new StringBuilder();
        foreach (var c in cards) header.Append(c);
        header.Append("END".PadRight(80));
        while (header.Length % 2880 != 0) header.Append(' ');
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(header.ToString()));
        bytes.AddRange(data);
        while (bytes.Count % 2880 != 0) bytes.Add(0);
        return [.. bytes];
    }

    private static List<string> ImageCards(int bitpix, int channels, bool primary = true, params string[] extra)
    {
        var cards = new List<string>
        {
            primary ? Card("SIMPLE", true) : Card("XTENSION", "IMAGE"),
            Card("BITPIX", bitpix),
            Card("NAXIS", channels > 1 ? 3 : 2),
            Card("NAXIS1", W),
            Card("NAXIS2", H),
        };
        if (channels > 1) cards.Add(Card("NAXIS3", channels));
        if (!primary) { cards.Add(Card("PCOUNT", 0)); cards.Add(Card("GCOUNT", 1)); }
        cards.Add(Card("OBJECT", "M42", "target"));
        cards.Add("HISTORY made by the tests".PadRight(80));
        cards.AddRange(extra);
        return cards;
    }

    private string Write(params byte[][] hdus)
    {
        string path = Path.Combine(_dir, Guid.NewGuid() + ".fits");
        File.WriteAllBytes(path, hdus.SelectMany(h => h).ToArray());
        return path;
    }

    /// <summary>Big-endian samples, plane after plane, from stored values indexed by (channel, pixel).</summary>
    private static byte[] Samples(int bitpix, int channels, Func<int, int, double> stored)
    {
        int bps = Math.Abs(bitpix) / 8;
        var data = new byte[W * H * channels * bps];
        for (int c = 0; c < channels; c++)
            for (int i = 0; i < W * H; i++)
            {
                var s = data.AsSpan((c * W * H + i) * bps, bps);
                double v = stored(c, i);
                switch (bitpix)
                {
                    case 8: s[0] = (byte)v; break;
                    case 16: BinaryPrimitives.WriteInt16BigEndian(s, (short)v); break;
                    case 32: BinaryPrimitives.WriteInt32BigEndian(s, (int)v); break;
                    case -32: BinaryPrimitives.WriteSingleBigEndian(s, (float)v); break;
                    default: BinaryPrimitives.WriteDoubleBigEndian(s, v); break;
                }
            }
        return data;
    }

    private static float Fraction(int c, int i) => (c * W * H + i) / (float)(3 * W * H);

    [Fact]
    public void ReadsFloat32Rgb()
    {
        var path = Write(Hdu(ImageCards(-32, 3), Samples(-32, 3, (c, i) => Fraction(c, i))));
        var f = FitsFile.Load(path);

        Assert.Equal((W, H, 3), (f.Width, f.Height, f.Channels));
        Assert.True(f.IsColour);
        Assert.Equal("RGB", f.ColorSpace);
        Assert.Equal("Float32", f.SampleFormatName);
        for (int c = 0; c < 3; c++)
            for (int i = 0; i < W * H; i++)
                Assert.Equal(Fraction(c, i), f.Planes[c][i], 6);
    }

    [Fact]
    public void ReadsUnsigned16BitWithStandardOffset()
    {
        var cards = ImageCards(16, 1, true, Card("BZERO", 32768), Card("BSCALE", 1));
        var path = Write(Hdu(cards, Samples(16, 1, (_, i) => i * 1000 - 32768)));
        var f = FitsFile.Load(path);

        Assert.Equal("UInt16", f.SampleFormatName);
        Assert.False(f.IsColour);
        for (int i = 0; i < W * H; i++)
            Assert.Equal(i * 1000 / 65535f, f.Planes[0][i], 6);
    }

    [Theory]
    [InlineData(8, 255.0, "UInt8")]
    [InlineData(16, 32767.0, "Int16")]
    [InlineData(32, 2147483647.0, "Int32")]
    public void ReadsIntegersByTheirTypeRange(int bitpix, double max, string name)
    {
        var path = Write(Hdu(ImageCards(bitpix, 1), Samples(bitpix, 1, (_, i) => Math.Round(i / 34.0 * max))));
        var f = FitsFile.Load(path);

        Assert.Equal(name, f.SampleFormatName);
        for (int i = 0; i < W * H; i++)
            Assert.Equal(Math.Round(i / 34.0 * max) / max, f.Planes[0][i], 5);
    }

    [Fact]
    public void ScalesFloatsInThe65535Range()
    {
        var path = Write(Hdu(ImageCards(-32, 1), Samples(-32, 1, (_, i) => i * 1000.0)));
        var f = FitsFile.Load(path);
        Assert.Equal(34000 / 65535f, f.Planes[0][34], 6);
    }

    [Fact]
    public void ReplacesNonFiniteValuesWithZero()
    {
        var path = Write(Hdu(ImageCards(-32, 1), Samples(-32, 1, (_, i) => i == 3 ? double.NaN : 0.5)));
        var f = FitsFile.Load(path);
        Assert.Equal(0f, f.Planes[0][3]);
        Assert.Equal(0.5f, f.Planes[0][4]);
    }

    [Fact]
    public void FlipsBottomUpImagesAndWritesThemBackBottomUp()
    {
        var cards = ImageCards(-32, 1, true, Card("ROWORDER", "BOTTOM-UP"));
        var data = Samples(-32, 1, (_, i) => i / 100.0);
        var path = Write(Hdu(cards, data));
        var f = FitsFile.Load(path);

        Assert.True(f.BottomUp);
        // The file's last row is shown first.
        Assert.Equal((H - 1) * W / 100f, f.Planes[0][0], 6);

        var output = Path.Combine(_dir, "out.fits");
        f.Save(output);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(output));
    }

    [Fact]
    public void FindsImageInExtensionAndPreservesOtherHdus()
    {
        var primary = Hdu([Card("SIMPLE", true), Card("BITPIX", 8), Card("NAXIS", 0), Card("EXTEND", true)], []);
        var image = Hdu(ImageCards(-32, 3, primary: false), Samples(-32, 3, (c, i) => Fraction(c, i)));
        var table = Hdu([Card("XTENSION", "BINTABLE"), Card("BITPIX", 8), Card("NAXIS", 2), Card("NAXIS1", 4),
            Card("NAXIS2", 2), Card("PCOUNT", 0), Card("GCOUNT", 1), Card("TFIELDS", 1)], [1, 2, 3, 4, 5, 6, 7, 8]);
        var path = Write(primary, image, table);

        var f = FitsFile.Load(path);
        Assert.Equal(3, f.Channels);
        Assert.Equal(Fraction(2, 10), f.Planes[2][10], 6);

        var edited = f.Planes.Select(p => p.Select(v => v / 2).ToArray()).ToArray();
        var output = Path.Combine(_dir, "out.fits");
        f.Save(output, edited);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(primary, bytes[..primary.Length]);
        Assert.Equal(table, bytes[^table.Length..]);
        var reloaded = FitsFile.Load(output);
        Assert.Equal(Fraction(2, 10) / 2, reloaded.Planes[2][10], 6);
    }

    [Fact]
    public void SaveKeepsHeaderAndFormatAndDropsChecksums()
    {
        var cards = ImageCards(16, 3, true, Card("BZERO", 32768), Card("BSCALE", 1),
            Card("CHECKSUM", "abc"), Card("DATASUM", "123"));
        var path = Write(Hdu(cards, Samples(16, 3, (c, i) => (c * 35 + i) * 500 - 32768)));
        var f = FitsFile.Load(path);

        var edited = f.Planes.Select(p => p.Select(v => Math.Min(1f, v * 1.5f)).ToArray()).ToArray();
        edited[0][0] = -0.2f; // out of range: clamped
        var output = Path.Combine(_dir, "out.fits");
        f.Save(output, edited);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(0, bytes.Length % 2880);
        string header = Encoding.ASCII.GetString(bytes, 0, 2880);
        Assert.Contains(Card("OBJECT", "M42", "target"), header);
        Assert.Contains("HISTORY made by the tests", header);
        Assert.DoesNotContain("CHECKSUM", header);
        Assert.DoesNotContain("DATASUM", header);

        var reloaded = FitsFile.Load(output);
        Assert.Equal("UInt16", reloaded.SampleFormatName);
        Assert.Equal(0f, reloaded.Planes[0][0]);
        for (int c = 0; c < 3; c++)
            for (int i = 1; i < W * H; i++)
                Assert.Equal(Math.Round(edited[c][i] * 65535) / 65535, reloaded.Planes[c][i], 6);
    }

    [Fact]
    public void RejectsFilesThatAreNotFits()
    {
        var path = Path.Combine(_dir, "bad.fits");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 5000)));
        Assert.Throws<InvalidDataException>(() => FitsFile.Load(path));
    }

    [Fact]
    public void ReportsCompressedImagesAsUnsupported()
    {
        var primary = Hdu([Card("SIMPLE", true), Card("BITPIX", 8), Card("NAXIS", 0), Card("EXTEND", true)], []);
        var table = Hdu([Card("XTENSION", "BINTABLE"), Card("BITPIX", 8), Card("NAXIS", 2), Card("NAXIS1", 8),
            Card("NAXIS2", 1), Card("PCOUNT", 0), Card("GCOUNT", 1), Card("TFIELDS", 1), Card("ZIMAGE", true)], new byte[8]);
        var ex = Assert.Throws<NotSupportedException>(() => FitsFile.Load(Write(primary, table)));
        Assert.Contains("fpack", ex.Message);
    }

    [Fact]
    public void AstroImageFileChoosesTheReaderByExtension()
    {
        var path = Write(Hdu(ImageCards(-32, 1), Samples(-32, 1, (_, i) => 0.25)));
        var fit = Path.ChangeExtension(path, ".FIT");
        File.Move(path, fit);
        Assert.IsType<FitsFile>(AstroImageFile.Load(fit));
    }
}
