using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using AstroColour.Core.Xisf;
using K4os.Compression.LZ4;

namespace AstroColour.Tests;

public class XisfFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("xisf-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int W = 7, H = 5, C = 3;

    private static float Expected(int c, int i) => (c * W * H + i) / (float)(C * W * H);

    /// <summary>Planar float32 samples matching <see cref="Expected"/>.</summary>
    private static byte[] Float32Planar()
    {
        var data = new byte[W * H * C * 4];
        for (int c = 0; c < C; c++)
            for (int i = 0; i < W * H; i++)
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan((c * W * H + i) * 4), Expected(c, i));
        return data;
    }

    /// <summary>Builds a monolithic XISF file by hand with one attached image block.</summary>
    private string WriteXisf(byte[] block, string imageAttributes, string extraXml = "", byte[]? extraBlock = null)
    {
        const int dataStart = 4096;
        int extraStart = dataStart + block.Length;
        string xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\">" +
            $"<Image geometry=\"{W}:{H}:{C}\" colorSpace=\"RGB\" location=\"attachment:{dataStart}:{block.Length}\" {imageAttributes}>" +
            "<FITSKeyword name=\"OBJECT\" value=\"'M42'\" comment=\"\"/>" +
            (extraBlock is null ? "" : $"<ICCProfile location=\"attachment:{extraStart}:{extraBlock.Length}\"/>") +
            "</Image>" + extraXml +
            "</xisf>";
        var xmlBytes = Encoding.UTF8.GetBytes(xml);

        var path = Path.Combine(_dir, Guid.NewGuid() + ".xisf");
        using var fs = File.Create(path);
        fs.Write("XISF0100"u8);
        var len = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)xmlBytes.Length);
        fs.Write(len);
        fs.Write(xmlBytes);
        fs.Write(new byte[dataStart - fs.Position]);
        fs.Write(block);
        if (extraBlock is not null) fs.Write(extraBlock);
        return path;
    }

    private static void AssertMatchesExpected(XisfFile f, float tolerance = 1e-6f)
    {
        Assert.Equal(W, f.Width);
        Assert.Equal(H, f.Height);
        Assert.Equal(C, f.Channels);
        for (int c = 0; c < C; c++)
            for (int i = 0; i < W * H; i++)
                Assert.InRange(f.Planes[c][i], Expected(c, i) - tolerance, Expected(c, i) + tolerance);
    }

    [Fact]
    public void Reads_uncompressed_float32_planar()
    {
        var f = XisfFile.Load(WriteXisf(Float32Planar(), "sampleFormat=\"Float32\" bounds=\"0:1\""));
        AssertMatchesExpected(f);
        Assert.Equal(XisfSampleFormat.Float32, f.SampleFormat);
    }

    [Fact]
    public void Reads_uint16_normal_pixel_storage()
    {
        var data = new byte[W * H * C * 2];
        for (int i = 0; i < W * H; i++)
            for (int c = 0; c < C; c++)
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan((i * C + c) * 2), (ushort)Math.Round(Expected(c, i) * 65535));

        var f = XisfFile.Load(WriteXisf(data, "sampleFormat=\"UInt16\" pixelStorage=\"Normal\""));
        AssertMatchesExpected(f, 1e-4f);
    }

    [Fact]
    public void Applies_float_bounds()
    {
        var data = Float32Planar();
        for (int k = 0; k < data.Length; k += 4)
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(k), BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(k)) * 100f);

        var f = XisfFile.Load(WriteXisf(data, "sampleFormat=\"Float32\" bounds=\"0:100\""));
        AssertMatchesExpected(f, 1e-5f);
    }

    [Theory]
    [InlineData("zlib", false)]
    [InlineData("zlib", true)]
    [InlineData("lz4", false)]
    [InlineData("lz4hc", true)]
    [InlineData("zstd", true)]
    public void Reads_compressed_images(string codec, bool shuffle)
    {
        var raw = Float32Planar();
        var input = shuffle ? XisfFile.Shuffle(raw, 4) : raw;
        var compressed = Compress(codec, input);
        string spec = shuffle ? $"{codec}+sh:{raw.Length}:4" : $"{codec}:{raw.Length}";

        var f = XisfFile.Load(WriteXisf(compressed, $"sampleFormat=\"Float32\" bounds=\"0:1\" compression=\"{spec}\""));
        AssertMatchesExpected(f);
    }

    [Fact]
    public void Reads_compressed_subblocks()
    {
        var raw = XisfFile.Shuffle(Float32Planar(), 4);
        int half = raw.Length / 2;
        var a = Compress("zlib", raw[..half]);
        var b = Compress("zlib", raw[half..]);
        var block = a.Concat(b).ToArray();

        var f = XisfFile.Load(WriteXisf(block,
            $"sampleFormat=\"Float32\" compression=\"zlib+sh:{raw.Length}:4\" subblocks=\"{a.Length},{half}:{b.Length},{raw.Length - half}\""));
        AssertMatchesExpected(f);
    }

    [Theory]
    [InlineData("Float32")]
    [InlineData("UInt16")]
    [InlineData("UInt8")]
    public void Round_trips_edited_pixels_and_preserves_metadata(string format)
    {
        var src = XisfFile.Load(WriteXisf(Float32Planar(), "sampleFormat=\"Float32\" bounds=\"0:1\"",
            extraBlock: [1, 2, 3, 4, 5]));
        var edited = src.Planes.Select(p => p.Select(v => 1f - v).ToArray()).ToArray();

        // Save once in the source format, then exercise integer formats via a re-load.
        var path = Path.Combine(_dir, "out.xisf");
        src.Save(path, edited);
        var reloaded = XisfFile.Load(path);
        Assert.Equal(XisfSampleFormat.Float32, reloaded.SampleFormat);
        for (int c = 0; c < C; c++)
            Assert.Equal(edited[c], reloaded.Planes[c]);

        var header = ReadHeader(path);
        var image = header.Root!.Element(XisfFile.Ns + "Image")!;
        Assert.Equal("OBJECT", (string?)image.Element(XisfFile.Ns + "FITSKeyword")?.Attribute("name"));
        Assert.Null(image.Attribute("compression"));

        // The ICC profile attachment must be copied byte-for-byte to its new location.
        var icc = ((string)image.Element(XisfFile.Ns + "ICCProfile")!.Attribute("location")!).Split(':');
        var bytes = File.ReadAllBytes(path).AsSpan(int.Parse(icc[1]), int.Parse(icc[2])).ToArray();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, bytes);

        if (format != "Float32")
        {
            var intData = new byte[W * H * C * (format == "UInt16" ? 2 : 1)];
            var intPath = WriteXisf(intData, $"sampleFormat=\"{format}\"");
            var intFile = XisfFile.Load(intPath);
            intFile.Save(intPath, edited);
            var intReloaded = XisfFile.Load(intPath);
            float tol = format == "UInt8" ? 0.5f / 255f + 1e-6f : 0.5f / 65535f + 1e-6f;
            for (int c = 0; c < C; c++)
                for (int i = 0; i < W * H; i++)
                    Assert.InRange(intReloaded.Planes[c][i], edited[c][i] - tol, edited[c][i] + tol);
        }
    }

    [Fact]
    public void Save_can_overwrite_the_source_file()
    {
        var path = WriteXisf(Compress("zlib", Float32Planar()), $"sampleFormat=\"Float32\" compression=\"zlib:{W * H * C * 4}\"");
        var src = XisfFile.Load(path);
        src.Save(path);
        AssertMatchesExpected(XisfFile.Load(path));
    }

    [Fact]
    public void Rejects_non_xisf_files()
    {
        var path = Path.Combine(_dir, "bad.xisf");
        File.WriteAllBytes(path, new byte[64]);
        Assert.Throws<InvalidDataException>(() => XisfFile.Load(path));
    }

    private static XDocument ReadHeader(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        return XDocument.Parse(Encoding.UTF8.GetString(bytes, 16, len));
    }

    private static byte[] Compress(string codec, byte[] data)
    {
        switch (codec)
        {
            case "zlib":
                using (var ms = new MemoryStream())
                {
                    using (var z = new ZLibStream(ms, CompressionLevel.Optimal)) z.Write(data);
                    return ms.ToArray();
                }
            case "lz4":
            case "lz4hc":
                var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
                int n = LZ4Codec.Encode(data, target, codec == "lz4hc" ? LZ4Level.L09_HC : LZ4Level.L00_FAST);
                return target[..n];
            default:
                using (var zstd = new ZstdSharp.Compressor())
                    return zstd.Wrap(data).ToArray();
        }
    }
}
