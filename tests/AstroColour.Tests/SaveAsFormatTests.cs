using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AstroColour.Core;
using AstroColour.Core.Fits;
using AstroColour.Core.Tiff;
using AstroColour.Core.Xisf;
using SkiaSharp;

namespace AstroColour.Tests;

public class SaveAsFormatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("save-as-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int W = 29, H = 17;

    private static float Value(int c, int i) => (c * W * H + i) / (float)(3 * W * H);

    private static float[][] Planes(int channels) =>
        Enumerable.Range(0, channels).Select(c => Enumerable.Range(0, W * H).Select(i => Value(c, i)).ToArray()).ToArray();

    private static readonly FitsKeyword[] Keywords =
    [
        new("OBJECT", "'M42'", "target"),
        new("EXPTIME", "300.", "seconds"),
        new("CRVAL1", "83.82", "RA of the reference pixel"),
        new("HISTORY", "", "stacked"),
    ];

    private string Output(string extension) => Path.Combine(_dir, Guid.NewGuid() + extension);

    private IAstroImage Source(ImageFormat format, int channels)
    {
        string path = format switch
        {
            ImageFormat.Fits => Output(".fits"),
            ImageFormat.Tiff => Output(".tif"),
            _ => Output(".xisf"),
        };
        switch (format)
        {
            case ImageFormat.Fits: FitsFile.Create(W, H, Planes(channels), 16, Keywords).Save(path); break;
            case ImageFormat.Tiff: TiffFile.Create(W, H, Planes(channels), 8, isFloat: false).Save(path); break;
            default: XisfFile.Create(W, H, Planes(channels), XisfSampleFormat.Float32, Keywords).Save(path); break;
        }
        return AstroImageFile.Load(path);
    }

    private static void AssertPixels(IAstroImage image, int channels, float tolerance)
    {
        Assert.Equal((W, H, channels), (image.Width, image.Height, image.Channels));
        for (int c = 0; c < channels; c++)
            for (int i = 0; i < W * H; i++)
                Assert.True(Math.Abs(image.Planes[c][i] - Value(c, i)) <= tolerance, $"channel {c}, pixel {i}: {image.Planes[c][i]} vs {Value(c, i)}");
    }

    [Theory]
    [InlineData(ImageFormat.Xisf, ".fits")]
    [InlineData(ImageFormat.Xisf, ".tif")]
    [InlineData(ImageFormat.Fits, ".xisf")]
    [InlineData(ImageFormat.Fits, ".tiff")]
    [InlineData(ImageFormat.Tiff, ".fit")]
    [InlineData(ImageFormat.Tiff, ".xisf")]
    public void Converts_between_astro_formats_keeping_the_sample_format(ImageFormat from, string extension)
    {
        foreach (int channels in new[] { 1, 3 })
        {
            var source = Source(from, channels);
            string path = Output(extension);
            AstroImageFile.Save(source, path);

            var saved = AstroImageFile.Load(path);
            Assert.Equal(AstroImageFile.FormatOf(path), AstroImageFile.FormatOf(saved));
            Assert.Equal(source.SampleFormatName, saved.SampleFormatName);
            Assert.Equal(channels == 3, saved.IsColour);
            AssertPixels(saved, channels, source.BitsPerSample == 8 ? 0.5f / 255 + 1e-6f : 0.5f / 65535 + 1e-6f);
        }
    }

    [Fact]
    public void Fits_keywords_carry_between_fits_and_xisf()
    {
        var fits = Source(ImageFormat.Fits, 3);
        string xisfPath = Output(".xisf");
        AstroImageFile.Save(fits, xisfPath);
        var xisf = AstroImageFile.Load(xisfPath);
        Assert.Equal(Keywords, xisf.FitsKeywords);

        string fitsPath = Output(".fits");
        AstroImageFile.Save(xisf, fitsPath);
        var cards = AstroImageFile.Load(fitsPath).FitsKeywords;
        Assert.Equal(Keywords, cards.Where(k => !k.IsLayout));
        Assert.Contains(new FitsKeyword("BZERO", "32768", "offset for unsigned integers"), cards);
    }

    [Fact]
    public void Rotating_while_converting_turns_the_image_and_drops_the_wcs()
    {
        var source = Source(ImageFormat.Fits, 1);
        string path = Output(".xisf");
        AstroImageFile.Save(source, path, rotation: Rotation.Clockwise);

        var saved = AstroImageFile.Load(path);
        Assert.Equal((H, W), (saved.Width, saved.Height));
        Assert.DoesNotContain(saved.FitsKeywords, k => k.Name == "CRVAL1");
        Assert.Contains(saved.FitsKeywords, k => k.Name == "OBJECT");
        // The source's top-left pixel ends up at the top right.
        Assert.Equal(Value(0, 0), saved.Planes[0][H - 1], 1e-4f);
    }

    [Fact]
    public void New_xisf_files_have_the_required_metadata()
    {
        string path = Output(".xisf");
        AstroImageFile.Save(Source(ImageFormat.Tiff, 3), path);
        var bytes = File.ReadAllBytes(path);
        string xml = Encoding.UTF8.GetString(bytes, 16, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        Assert.Contains("XISF:CreationTime", xml);
        Assert.Contains("XISF:CreatorApplication", xml);
        Assert.Contains("colorSpace=\"RGB\"", xml);
    }

    [Theory]
    [InlineData(ImageFormat.Fits, 3, 16)]
    [InlineData(ImageFormat.Xisf, 1, 16)]
    [InlineData(ImageFormat.Tiff, 3, 8)]
    [InlineData(ImageFormat.Tiff, 1, 8)]
    public void Saves_png_at_the_source_bit_depth(ImageFormat from, int channels, int expectedBits)
    {
        var source = Source(from, channels);
        string path = Output(".png");
        AstroImageFile.Save(source, path, rotation: Rotation.HalfTurn);

        var (width, height, bits, colourType, samples) = DecodePng(path);
        Assert.Equal((W, H, expectedBits, channels == 3 ? 2 : 0), (width, height, bits, colourType));
        float max = (1 << bits) - 1;
        for (int c = 0; c < channels; c++)
            for (int i = 0; i < W * H; i++)
                Assert.Equal(Value(c, W * H - 1 - i), samples[i * channels + c] / max, 0.5f / max + 1e-4f);

        // A full decoder (which checks the CRCs) reads it too.
        using var bitmap = SKBitmap.Decode(path);
        Assert.NotNull(bitmap);
        Assert.Equal((W, H), (bitmap.Width, bitmap.Height));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Saves_jpeg(int channels)
    {
        var source = Source(ImageFormat.Xisf, channels);
        string path = Output(".JPG");
        AstroImageFile.Save(source, path);

        using var codec = SKCodec.Create(path);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        using var bitmap = SKBitmap.Decode(path, new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int i = 0; i < W * H; i++)
        {
            var pixel = bitmap.GetPixel(i % W, i / W);
            Assert.Equal(Value(0, i) * 255, pixel.Red, tolerance: 4.0);
            Assert.Equal(Value(channels == 3 ? 2 : 0, i) * 255, pixel.Blue, tolerance: 4.0);
        }
    }

    [Fact]
    public void Saving_in_the_same_format_keeps_the_original_file_layout()
    {
        var source = Source(ImageFormat.Fits, 3);
        string path = Output(".fts");
        AstroImageFile.Save(source, path);
        Assert.IsType<FitsFile>(AstroImageFile.Load(path));
        Assert.Equal(source.FitsKeywords, AstroImageFile.Load(path).FitsKeywords);
    }

    [Fact]
    public void Rejects_unknown_extensions()
    {
        Assert.Throws<NotSupportedException>(() => AstroImageFile.Save(Source(ImageFormat.Fits, 1), Output(".bmp")));
    }

    [Fact]
    public void Offers_the_current_format_first()
    {
        var types = AstroImageFile.SaveFileTypes(Source(ImageFormat.Tiff, 3), "C:/images/m42.TIFF");
        Assert.Equal([ImageFormat.Tiff, ImageFormat.Xisf, ImageFormat.Fits, ImageFormat.Png, ImageFormat.Jpeg], types.Select(t => t.Format));
        Assert.Equal([".tiff", ".tif"], types[0].Extensions);

        // After a Save As to PNG, the PNG file is what is being edited.
        Assert.Equal(ImageFormat.Png, AstroImageFile.SaveFileTypes(Source(ImageFormat.Tiff, 3), "C:/images/m42.png")[0].Format);
    }

    /// <summary>A minimal PNG decoder (non-interlaced greyscale or truecolour) that undoes the row filters.</summary>
    private static (int Width, int Height, int Bits, int ColourType, int[] Samples) DecodePng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int width = 0, height = 0, bits = 0, colourType = 0;
        var idat = new MemoryStream();
        for (int p = 8; p < bytes.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(p));
            string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
            var data = bytes.AsSpan(p + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bits = data[8];
                colourType = data[9];
            }
            else if (type == "IDAT") idat.Write(data);
            p += 12 + length;
        }

        idat.Position = 0;
        var raw = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress)) zlib.CopyTo(raw);
        var filtered = raw.ToArray();

        int channels = colourType == 2 ? 3 : 1;
        int bpp = channels * bits / 8;
        int rowBytes = width * bpp;
        var image = new byte[height * rowBytes];
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[y * (rowBytes + 1)];
            for (int i = 0; i < rowBytes; i++)
            {
                int a = i >= bpp ? image[y * rowBytes + i - bpp] : 0;
                int b = y > 0 ? image[(y - 1) * rowBytes + i] : 0;
                int c = i >= bpp && y > 0 ? image[(y - 1) * rowBytes + i - bpp] : 0;
                int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                int predicted = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                };
                image[y * rowBytes + i] = (byte)(filtered[y * (rowBytes + 1) + 1 + i] + predicted);
            }
        }

        var samples = bits == 16
            ? Enumerable.Range(0, image.Length / 2).Select(k => (int)BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(k * 2))).ToArray()
            : image.Select(v => (int)v).ToArray();
        return (width, height, bits, colourType, samples);
    }
}
