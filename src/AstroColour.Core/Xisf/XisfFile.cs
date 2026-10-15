using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AstroColour.Core.Fits;
using K4os.Compression.LZ4;

namespace AstroColour.Core.Xisf;

public enum XisfSampleFormat { UInt8, UInt16, UInt32, Float32, Float64 }

/// <summary>
/// A monolithic XISF 1.0 file. The first Image element is decoded into normalised
/// [0,1] float planes; everything else in the header (properties, FITS keywords,
/// ICC profiles, further images) is preserved verbatim when the file is saved.
/// </summary>
public sealed class XisfFile : IAstroImage
{
    public static readonly XNamespace Ns = "http://www.pixinsight.com/xisf";
    private const string Signature = "XISF0100";
    private const int BlockAlignment = 4096;

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public XisfSampleFormat SampleFormat { get; }
    public string ColorSpace { get; }
    public string SampleFormatName => SampleFormat.ToString();
    public double BoundsLow { get; }
    public double BoundsHigh { get; }

    /// <summary>Pixel data, one plane per channel, normalised to [0,1] (floats may exceed this range).</summary>
    public float[][] Planes { get; }

    public int BitsPerSample => BytesPerSample(SampleFormat) * 8;
    public bool IsFloat => SampleFormat is XisfSampleFormat.Float32 or XisfSampleFormat.Float64;

    public IReadOnlyList<FitsKeyword> FitsKeywords =>
        _header.Root!.Elements(Ns + "Image").ElementAt(_imageIndex).Elements(Ns + "FITSKeyword")
            .Select(k => new FitsKeyword((string?)k.Attribute("name") ?? "", (string?)k.Attribute("value") ?? "", (string?)k.Attribute("comment") ?? ""))
            .ToList();

    public bool IsColour => Channels >= 3;
    public int PixelCount => Width * Height;

    private readonly XDocument _header;
    private readonly int _imageIndex;
    // Raw (still compressed) bytes of every other attachment, keyed by the element's
    // position in Root.DescendantsAndSelf(), so they can be found again in a cloned header.
    private readonly List<(int Index, byte[] Data)> _otherAttachments;

    private XisfFile(XDocument header, int imageIndex, int width, int height, int channels,
        XisfSampleFormat format, string colorSpace, double lo, double hi, float[][] planes,
        List<(int, byte[])> otherAttachments)
    {
        _header = header;
        _imageIndex = imageIndex;
        Width = width;
        Height = height;
        Channels = channels;
        SampleFormat = format;
        ColorSpace = colorSpace;
        BoundsLow = lo;
        BoundsHigh = hi;
        Planes = planes;
        _otherAttachments = otherAttachments;
    }

    /// <summary>
    /// A new XISF file holding <paramref name="planes"/> (Gray, or RGB with three planes) in
    /// <paramref name="format"/>, with <paramref name="keywords"/> as the image's FITS keywords.
    /// </summary>
    public static XisfFile Create(int width, int height, float[][] planes, XisfSampleFormat format, IEnumerable<FitsKeyword> keywords)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        int channels = planes.Length;
        string colorSpace = channels >= 3 ? "RGB" : "Gray";
        bool isFloat = format is XisfSampleFormat.Float32 or XisfSampleFormat.Float64;

        var image = new XElement(Ns + "Image",
            new XAttribute("geometry", $"{width}:{height}:{channels}"),
            new XAttribute("sampleFormat", format.ToString()),
            isFloat ? new XAttribute("bounds", "0:1") : null,
            new XAttribute("colorSpace", colorSpace),
            keywords.Select(k => new XElement(Ns + "FITSKeyword",
                new XAttribute("name", k.Name), new XAttribute("value", k.Value), new XAttribute("comment", k.Comment))));
        var metadata = new XElement(Ns + "Metadata",
            new XElement(Ns + "Property", new XAttribute("id", "XISF:CreationTime"), new XAttribute("type", "TimePoint"),
                new XAttribute("value", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture))),
            new XElement(Ns + "Property", new XAttribute("id", "XISF:CreatorApplication"), new XAttribute("type", "String"), "Astro Colour Editor"));
        var root = new XElement(Ns + "xisf",
            new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            new XAttribute(xsi + "schemaLocation", "http://www.pixinsight.com/xisf http://pixinsight.com/xisf/xisf-1.0.xsd"),
            image, metadata);

        return new XisfFile(new XDocument(new XDeclaration("1.0", "UTF-8", null), root), 0, width, height, channels,
            format, colorSpace, 0, 1, planes, []);
    }

    // ---------------------------------------------------------------- loading

    public static XisfFile Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Load(fs);
    }

    public static XisfFile Load(Stream fs)
    {
        Span<byte> preamble = stackalloc byte[16];
        fs.ReadExactly(preamble);
        if (Encoding.ASCII.GetString(preamble[..8]) != Signature)
            throw new InvalidDataException("Not a monolithic XISF 1.0 file (bad signature).");

        uint headerLength = BinaryPrimitives.ReadUInt32LittleEndian(preamble[8..12]);
        if (headerLength == 0 || headerLength > fs.Length - 16)
            throw new InvalidDataException("Invalid XISF header length.");

        var xmlBytes = new byte[headerLength];
        fs.ReadExactly(xmlBytes);
        int xmlLength = xmlBytes.Length;
        while (xmlLength > 0 && xmlBytes[xmlLength - 1] == 0) xmlLength--;

        XDocument doc;
        using (var ms = new MemoryStream(xmlBytes, 0, xmlLength))
            doc = XDocument.Load(ms);

        var root = doc.Root ?? throw new InvalidDataException("Empty XISF header.");
        var images = root.Elements(Ns + "Image").ToList();
        if (images.Count == 0)
            throw new InvalidDataException("The file does not contain an image.");

        const int imageIndex = 0;
        var image = images[imageIndex];

        var geometry = ((string?)image.Attribute("geometry") ?? "").Split(':');
        if (geometry.Length != 3)
            throw new NotSupportedException("Only two-dimensional images are supported.");
        int width = int.Parse(geometry[0]);
        int height = int.Parse(geometry[1]);
        int channels = int.Parse(geometry[2]);
        if (width <= 0 || height <= 0 || channels <= 0)
            throw new InvalidDataException("Invalid image geometry.");

        var format = ParseSampleFormat((string?)image.Attribute("sampleFormat"));
        string colorSpace = (string?)image.Attribute("colorSpace") ?? "Gray";
        bool planar = !string.Equals((string?)image.Attribute("pixelStorage"), "Normal", StringComparison.OrdinalIgnoreCase);
        bool bigEndian = string.Equals((string?)image.Attribute("byteOrder"), "big", StringComparison.OrdinalIgnoreCase);

        double lo = 0, hi = 1;
        if ((string?)image.Attribute("bounds") is { } bounds)
        {
            var b = bounds.Split(':');
            lo = double.Parse(b[0], System.Globalization.CultureInfo.InvariantCulture);
            hi = double.Parse(b[1], System.Globalization.CultureInfo.InvariantCulture);
            if (hi <= lo) { lo = 0; hi = 1; }
        }

        byte[] data = Decompress(ReadRawBlock(fs, image), image);
        long expected = (long)width * height * channels * BytesPerSample(format);
        if (data.LongLength < expected)
            throw new InvalidDataException($"Image data is truncated ({data.LongLength} of {expected} bytes).");

        var planes = DecodeSamples(data, width * height, channels, format, planar, bigEndian, lo, hi);

        var others = new List<(int, byte[])>();
        int index = 0;
        foreach (var el in root.DescendantsAndSelf())
        {
            if (el != image && IsAttachment(el))
                others.Add((index, ReadRawBlock(fs, el)));
            index++;
        }

        return new XisfFile(doc, imageIndex, width, height, channels, format, colorSpace, lo, hi, planes, others);
    }

    private static bool IsAttachment(XElement el) =>
        ((string?)el.Attribute("location"))?.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) == true;

    private static XisfSampleFormat ParseSampleFormat(string? s) => s?.ToLowerInvariant() switch
    {
        "uint8" => XisfSampleFormat.UInt8,
        "uint16" => XisfSampleFormat.UInt16,
        "uint32" => XisfSampleFormat.UInt32,
        "float32" => XisfSampleFormat.Float32,
        "float64" => XisfSampleFormat.Float64,
        _ => throw new NotSupportedException($"Unsupported sample format '{s}'."),
    };

    public static int BytesPerSample(XisfSampleFormat f) => f switch
    {
        XisfSampleFormat.UInt8 => 1,
        XisfSampleFormat.UInt16 => 2,
        XisfSampleFormat.UInt32 or XisfSampleFormat.Float32 => 4,
        _ => 8,
    };

    private static byte[] ReadRawBlock(Stream fs, XElement el)
    {
        string location = (string?)el.Attribute("location")
            ?? throw new InvalidDataException($"<{el.Name.LocalName}> has no location attribute.");
        var parts = location.Split(':');
        switch (parts[0].ToLowerInvariant())
        {
            case "attachment":
                long position = long.Parse(parts[1]);
                long size = long.Parse(parts[2]);
                if (position < 0 || size < 0 || position + size > fs.Length)
                    throw new InvalidDataException("Attachment lies outside the file.");
                var buffer = new byte[size];
                fs.Seek(position, SeekOrigin.Begin);
                fs.ReadExactly(buffer);
                return buffer;
            case "inline":
                string text = string.Concat(el.Nodes().OfType<XText>().Select(t => t.Value));
                return DecodeText(parts.Length > 1 ? parts[1] : "base64", text);
            case "embedded":
                var dataEl = el.Element(Ns + "Data") ?? throw new InvalidDataException("Embedded block has no <Data> element.");
                return DecodeText((string?)dataEl.Attribute("encoding") ?? "base64", dataEl.Value);
            default:
                throw new NotSupportedException($"Unsupported data block location '{location}'.");
        }
    }

    private static byte[] DecodeText(string encoding, string text)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return encoding.ToLowerInvariant() switch
        {
            "base64" => Convert.FromBase64String(clean),
            "hex" => Convert.FromHexString(clean),
            _ => throw new NotSupportedException($"Unsupported data encoding '{encoding}'."),
        };
    }

    private static byte[] Decompress(byte[] raw, XElement el)
    {
        var spec = (string?)el.Attribute("compression");
        if (string.IsNullOrEmpty(spec)) return raw;

        var parts = spec.Split(':');
        string codec = parts[0].ToLowerInvariant();
        bool shuffled = codec.EndsWith("+sh");
        if (shuffled) codec = codec[..^3];
        long uncompressedSize = long.Parse(parts[1]);
        int itemSize = shuffled && parts.Length > 2 ? int.Parse(parts[2]) : 1;

        var output = new byte[uncompressedSize];
        var subblocks = (string?)el.Attribute("subblocks");
        if (string.IsNullOrEmpty(subblocks))
        {
            DecompressBlock(codec, raw, 0, raw.Length, output, 0, output.Length);
        }
        else
        {
            int inOffset = 0, outOffset = 0;
            foreach (var pair in subblocks.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var sizes = pair.Split(',');
                int compressed = int.Parse(sizes[0]);
                int uncompressed = int.Parse(sizes[1]);
                DecompressBlock(codec, raw, inOffset, compressed, output, outOffset, uncompressed);
                inOffset += compressed;
                outOffset += uncompressed;
            }
        }

        return shuffled && itemSize > 1 ? Unshuffle(output, itemSize) : output;
    }

    private static void DecompressBlock(string codec, byte[] src, int srcOffset, int srcLength, byte[] dst, int dstOffset, int dstLength)
    {
        switch (codec)
        {
            case "zlib":
                using (var zs = new ZLibStream(new MemoryStream(src, srcOffset, srcLength), CompressionMode.Decompress))
                    zs.ReadExactly(dst, dstOffset, dstLength);
                break;
            case "lz4":
            case "lz4hc":
                int decoded = LZ4Codec.Decode(src, srcOffset, srcLength, dst, dstOffset, dstLength);
                if (decoded != dstLength) throw new InvalidDataException("LZ4 decompression failed.");
                break;
            case "zstd":
                using (var zstd = new ZstdSharp.Decompressor())
                {
                    int n = zstd.Unwrap(new ReadOnlySpan<byte>(src, srcOffset, srcLength), new Span<byte>(dst, dstOffset, dstLength));
                    if (n != dstLength) throw new InvalidDataException("Zstandard decompression failed.");
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported compression codec '{codec}'.");
        }
    }

    /// <summary>Reverses XISF byte shuffling: byte j of every item was stored contiguously.</summary>
    internal static byte[] Unshuffle(byte[] data, int itemSize)
    {
        var result = new byte[data.Length];
        int items = data.Length / itemSize;
        int s = 0;
        for (int j = 0; j < itemSize; j++)
            for (int i = 0; i < items; i++)
                result[i * itemSize + j] = data[s++];
        Array.Copy(data, s, result, s, data.Length - s);
        return result;
    }

    internal static byte[] Shuffle(byte[] data, int itemSize)
    {
        var result = new byte[data.Length];
        int items = data.Length / itemSize;
        int s = 0;
        for (int j = 0; j < itemSize; j++)
            for (int i = 0; i < items; i++)
                result[s++] = data[i * itemSize + j];
        Array.Copy(data, s, result, s, data.Length - s);
        return result;
    }

    private static float[][] DecodeSamples(byte[] data, int n, int channels, XisfSampleFormat format,
        bool planar, bool bigEndian, double lo, double hi)
    {
        var planes = new float[channels][];
        int bps = BytesPerSample(format);
        double scale = 1.0 / (hi - lo);

        Parallel.For(0, channels, c =>
        {
            var plane = new float[n];
            ReadOnlySpan<byte> bytes = data;
            for (int i = 0; i < n; i++)
            {
                long sample = planar ? (long)c * n + i : (long)i * channels + c;
                var s = bytes.Slice((int)(sample * bps), bps);
                plane[i] = format switch
                {
                    XisfSampleFormat.UInt8 => s[0] / 255f,
                    XisfSampleFormat.UInt16 => (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s)) / 65535f,
                    XisfSampleFormat.UInt32 => (float)((bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s)) / 4294967295.0),
                    XisfSampleFormat.Float32 => (float)(((bigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s)) - lo) * scale),
                    _ => (float)(((bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s)) - lo) * scale),
                };
            }
            planes[c] = plane;
        });
        return planes;
    }

    // ----------------------------------------------------------------- saving

    /// <summary>
    /// Writes the file, replacing the main image's pixels with <paramref name="planes"/>
    /// (or the current <see cref="Planes"/>) turned by <paramref name="rotation"/>. The image keeps its
    /// original sample format and is stored uncompressed and planar; all other header content is preserved,
    /// except that a rotated image loses its astrometric solution.
    /// </summary>
    public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None)
    {
        planes ??= Planes;
        if (planes.Length != Channels || planes.Any(p => p.Length != PixelCount))
            throw new ArgumentException("Plane data does not match the image geometry.", nameof(planes));
        planes = rotation.Apply(planes, Width, Height);

        var doc = new XDocument(_header);
        var root = doc.Root!;
        var all = root.DescendantsAndSelf().ToList();
        var image = root.Elements(Ns + "Image").ElementAt(_imageIndex);

        if (rotation != Rotation.None)
        {
            var (width, height) = rotation.Size(Width, Height);
            image.SetAttributeValue("geometry", $"{width}:{height}:{Channels}");
            image.Elements(Ns + "FITSKeyword")
                .Where(k => RotationExtensions.IsWcsKeyword((string?)k.Attribute("name") ?? ""))
                .Remove();
            image.Elements(Ns + "Property")
                .Where(p => ((string?)p.Attribute("id"))?.StartsWith("PCL:AstrometricSolution", StringComparison.Ordinal) == true)
                .Remove();
        }

        var blocks = new List<(XElement Element, byte[] Data)> { (image, EncodeSamples(planes)) };
        foreach (var (index, data) in _otherAttachments)
        {
            var el = all[index];
            // Thumbnails would no longer match the edited image, so they are dropped, as are removed properties.
            if (el.AncestorsAndSelf().Any(a => a.Name.LocalName == "Thumbnail") || el.Document != doc) continue;
            blocks.Add((el, data));
        }
        root.Descendants(Ns + "Thumbnail").ToList().Remove();

        foreach (var attr in new[] { "compression", "subblocks", "checksum", "byteOrder" })
            image.Attribute(attr)?.Remove();
        image.SetAttributeValue("pixelStorage", "Planar");
        image.Nodes().OfType<XText>().ToList().Remove();
        image.Elements(Ns + "Data").Remove();

        // The attachment positions are written into the header, so lay out the data
        // after the header and grow the reserved header space until it fits.
        long dataStart = BlockAlignment;
        byte[] xml;
        var positions = new long[blocks.Count];
        while (true)
        {
            long position = dataStart;
            for (int i = 0; i < blocks.Count; i++)
            {
                positions[i] = position;
                blocks[i].Element.SetAttributeValue("location", $"attachment:{position}:{blocks[i].Data.LongLength}");
                position = Align(position + blocks[i].Data.LongLength, BlockAlignment);
            }
            xml = SerializeHeader(doc);
            if (16 + xml.LongLength <= dataStart) break;
            dataStart = Align(16 + xml.LongLength, BlockAlignment);
        }

        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Span<byte> preamble = stackalloc byte[16];
            Encoding.ASCII.GetBytes(Signature, preamble);
            BinaryPrimitives.WriteUInt32LittleEndian(preamble[8..12], (uint)xml.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(preamble[12..16], 0);
            fs.Write(preamble);
            fs.Write(xml);
            for (int i = 0; i < blocks.Count; i++)
            {
                WriteZeros(fs, positions[i] - fs.Position);
                fs.Write(blocks[i].Data);
            }
        }
        File.Move(tempPath, fullPath, overwrite: true);
    }

    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private static void WriteZeros(Stream s, long count)
    {
        Span<byte> zeros = stackalloc byte[4096];
        zeros.Clear();
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            s.Write(zeros[..n]);
            count -= n;
        }
    }

    private static byte[] SerializeHeader(XDocument doc)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        using (var writer = XmlWriter.Create(ms, settings))
            doc.Save(writer);
        return ms.ToArray();
    }

    private byte[] EncodeSamples(float[][] planes)
    {
        int n = PixelCount;
        int bps = BytesPerSample(SampleFormat);
        var data = new byte[(long)n * Channels * bps];
        double range = BoundsHigh - BoundsLow;

        Parallel.For(0, Channels, c =>
        {
            var plane = planes[c];
            var bytes = new Span<byte>(data, c * n * bps, n * bps);
            for (int i = 0; i < n; i++)
            {
                var s = bytes.Slice(i * bps, bps);
                float v = plane[i];
                switch (SampleFormat)
                {
                    case XisfSampleFormat.UInt8:
                        s[0] = (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
                        break;
                    case XisfSampleFormat.UInt16:
                        BinaryPrimitives.WriteUInt16LittleEndian(s, (ushort)(Math.Clamp(v, 0f, 1f) * 65535f + 0.5f));
                        break;
                    case XisfSampleFormat.UInt32:
                        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)(Math.Clamp((double)v, 0, 1) * 4294967295.0 + 0.5));
                        break;
                    case XisfSampleFormat.Float32:
                        BinaryPrimitives.WriteSingleLittleEndian(s, (float)(v * range + BoundsLow));
                        break;
                    default:
                        BinaryPrimitives.WriteDoubleLittleEndian(s, v * range + BoundsLow);
                        break;
                }
            }
        });
        return data;
    }
}
