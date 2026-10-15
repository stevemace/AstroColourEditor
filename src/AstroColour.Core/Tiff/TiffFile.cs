using System.Globalization;
using System.Runtime.InteropServices;
using BitMiracle.LibTiff.Classic;
using LibTiff = BitMiracle.LibTiff.Classic.Tiff;

namespace AstroColour.Core.Tiff;

/// <summary>
/// A TIFF file. The first image (greyscale or RGB, optionally with extra channels such as alpha;
/// 8/16/32-bit integer or 16/32/64-bit float; strips or tiles) is decoded into normalised [0,1]
/// float planes. Saving writes the image with the same sample format, layout and compression, and
/// keeps its descriptive metadata (resolution, ICC profile, XMP, description, …). Further pages and
/// EXIF data are not kept.
/// </summary>
public sealed class TiffFile : IAstroImage
{
    // Lossless compressions that are written back as-is; anything else is saved with LZW.
    private static readonly Compression[] WritableCompressions =
        [Compression.NONE, Compression.LZW, Compression.DEFLATE, Compression.ADOBE_DEFLATE, Compression.PACKBITS];

    // Tags copied from the source file because they describe, rather than encode, the image.
    private static readonly TiffTag[] MetadataTags =
    [
        TiffTag.EXTRASAMPLES, TiffTag.ORIENTATION, TiffTag.XRESOLUTION, TiffTag.YRESOLUTION, TiffTag.RESOLUTIONUNIT,
        TiffTag.IMAGEDESCRIPTION, TiffTag.DOCUMENTNAME, TiffTag.PAGENAME, TiffTag.MAKE, TiffTag.MODEL,
        TiffTag.SOFTWARE, TiffTag.DATETIME, TiffTag.ARTIST, TiffTag.HOSTCOMPUTER, TiffTag.COPYRIGHT,
        TiffTag.ICCPROFILE, TiffTag.XMLPACKET, TiffTag.PHOTOSHOP, TiffTag.RICHTIFFIPTC,
    ];

    static TiffFile() => LibTiff.SetErrorHandler(new ThrowingErrorHandler());

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public SampleFormat SampleFormat { get; }
    public Compression Compression { get; }
    public bool IsFloat => SampleFormat == SampleFormat.IEEEFP;
    public bool IsColour => _photometric == Photometric.RGB;
    public string ColorSpace => IsColour ? "RGB" : "Gray";
    public string SampleFormatName => (SampleFormat == SampleFormat.IEEEFP ? "Float" : SampleFormat == SampleFormat.INT ? "Int" : "UInt") + BitsPerSample;

    public float[][] Planes { get; }
    public int PixelCount => Width * Height;

    private readonly Photometric _photometric;
    private readonly PlanarConfig _planarConfig;
    private readonly int _predictor;
    private readonly Dictionary<TiffTag, object[]> _metadata;

    // Stored value = normalised × (_high − _low) + _low; MINISWHITE files store 1 − normalised.
    private readonly double _low, _high;

    private int BytesPerSample => BitsPerSample / 8;

    private TiffFile(int width, int height, int channels, int bitsPerSample, SampleFormat sampleFormat,
        Photometric photometric, PlanarConfig planarConfig, Compression compression, int predictor,
        Dictionary<TiffTag, object[]> metadata, double low, double high, float[][] planes)
    {
        Width = width;
        Height = height;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        SampleFormat = sampleFormat;
        _photometric = photometric;
        _planarConfig = planarConfig;
        Compression = compression;
        _predictor = predictor;
        _metadata = metadata;
        _low = low;
        _high = high;
        Planes = planes;
    }

    /// <summary>
    /// A new greyscale (one plane) or RGB (three planes) TIFF holding <paramref name="planes"/> as unsigned
    /// integers or floats of <paramref name="bitsPerSample"/> bits, LZW-compressed.
    /// </summary>
    public static TiffFile Create(int width, int height, float[][] planes, int bitsPerSample, bool isFloat)
    {
        var photometric = planes.Length >= 3 ? Photometric.RGB : Photometric.MINISBLACK;
        double high = isFloat ? 1 : Math.Pow(2, bitsPerSample) - 1;
        return new TiffFile(width, height, planes.Length, bitsPerSample, isFloat ? SampleFormat.IEEEFP : SampleFormat.UINT,
            photometric, PlanarConfig.CONTIG, Compression.LZW, predictor: isFloat ? 1 : 2, [], 0, high, planes);
    }

    // ---------------------------------------------------------------- loading

    public static TiffFile Load(string path)
    {
        // The stream is ours so it is closed even when LibTiff throws part-way through opening.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var tif = LibTiff.ClientOpen(path, "r", fs, new TiffStream()) ?? throw new InvalidDataException("Not a TIFF file.");

        int width = Int(tif, TiffTag.IMAGEWIDTH);
        int height = Int(tif, TiffTag.IMAGELENGTH);
        int channels = Int(tif, TiffTag.SAMPLESPERPIXEL, 1);
        int bits = Int(tif, TiffTag.BITSPERSAMPLE, 1);
        var format = (SampleFormat)Int(tif, TiffTag.SAMPLEFORMAT, (int)SampleFormat.UINT);
        var photometric = (Photometric)Int(tif, TiffTag.PHOTOMETRIC, (int)Photometric.MINISBLACK);
        var planar = (PlanarConfig)Int(tif, TiffTag.PLANARCONFIG, (int)PlanarConfig.CONTIG);
        var compression = (Compression)Int(tif, TiffTag.COMPRESSION, (int)Compression.NONE);
        int predictor = Int(tif, TiffTag.PREDICTOR, 1);

        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The file does not contain an image.");
        if (photometric is not (Photometric.MINISBLACK or Photometric.MINISWHITE or Photometric.RGB))
            throw new NotSupportedException($"Unsupported TIFF colour model ({photometric}). Only greyscale and RGB images are supported.");
        if (photometric == Photometric.RGB && channels < 3)
            throw new InvalidDataException("RGB image has fewer than three channels.");
        bool supportedBits = format switch
        {
            SampleFormat.UINT or SampleFormat.INT => bits is 8 or 16 or 32,
            SampleFormat.IEEEFP => bits is 16 or 32 or 64,
            _ => false,
        };
        if (!supportedBits)
            throw new NotSupportedException($"Unsupported TIFF sample format ({bits}-bit {format}).");
        if ((long)width * height * (bits / 8) > Array.MaxLength)
            throw new NotSupportedException("The image is too large.");

        int bps = bits / 8;
        var raw = new byte[channels][];
        for (int c = 0; c < channels; c++) raw[c] = new byte[(long)width * height * bps];
        ReadSamples(tif, width, height, channels, bps, planar == PlanarConfig.SEPARATE, raw);

        var planes = new float[channels][];
        Parallel.For(0, channels, c => planes[c] = Decode(raw[c], format, bits));

        // Integers use their type's range; floats are usually [0,1], sometimes [0,65535].
        double low = 0, high;
        if (format == SampleFormat.UINT)
            high = Math.Pow(2, bits) - 1;
        else if (format == SampleFormat.INT)
            high = Math.Pow(2, bits - 1) - 1;
        else
        {
            float max = planes.Max(p => p.Length == 0 ? 0f : p.Max());
            high = max <= 1.5f ? 1 : max <= 65535 * 1.01f ? 65535 : max;
        }

        float scale = (float)(1 / (high - low));
        bool invert = photometric == Photometric.MINISWHITE;
        Parallel.For(0, channels, c =>
        {
            var plane = planes[c];
            for (int i = 0; i < plane.Length; i++)
            {
                float v = (float)((plane[i] - low) * scale);
                plane[i] = invert ? 1 - v : v;
            }
        });

        var metadata = new Dictionary<TiffTag, object[]>();
        foreach (var tag in MetadataTags)
            if (tif.GetField(tag) is { } values)
                metadata[tag] = values.Select(v => v.Value).ToArray();

        return new TiffFile(width, height, channels, bits, format, photometric, planar, compression, predictor,
            metadata, low, high, planes);
    }

    /// <summary>Reads every sample into one byte array per channel, row-major, in host byte order.</summary>
    private static void ReadSamples(LibTiff tif, int width, int height, int channels, int bps, bool separate, byte[][] raw)
    {
        int chunks = separate ? channels : 1;
        int samplesPerPixel = separate ? 1 : channels;

        if (!tif.IsTiled())
        {
            var line = new byte[tif.ScanlineSize()];
            for (int s = 0; s < chunks; s++)
                for (int y = 0; y < height; y++)
                {
                    if (!tif.ReadScanline(line, y, (short)s))
                        throw new InvalidDataException("Image data is truncated or corrupt.");
                    Scatter(line, 0, width, samplesPerPixel, s, raw, y * width, bps);
                }
            return;
        }

        int tileWidth = Int(tif, TiffTag.TILEWIDTH);
        int tileHeight = Int(tif, TiffTag.TILELENGTH);
        var tile = new byte[tif.TileSize()];
        int tileRowBytes = tileWidth * samplesPerPixel * bps;
        for (int s = 0; s < chunks; s++)
            for (int ty = 0; ty < height; ty += tileHeight)
                for (int tx = 0; tx < width; tx += tileWidth)
                {
                    if (tif.ReadTile(tile, 0, tx, ty, 0, (short)s) < 0)
                        throw new InvalidDataException("Image data is truncated or corrupt.");
                    int columns = Math.Min(tileWidth, width - tx);
                    int rows = Math.Min(tileHeight, height - ty);
                    for (int r = 0; r < rows; r++)
                        Scatter(tile, r * tileRowBytes, columns, samplesPerPixel, s, raw, (ty + r) * width + tx, bps);
                }
    }

    /// <summary>Copies <paramref name="pixels"/> pixels of interleaved samples into the per-channel arrays.</summary>
    private static void Scatter(byte[] src, int offset, int pixels, int samplesPerPixel, int firstChannel,
        byte[][] raw, int firstPixel, int bps)
    {
        for (int k = 0; k < samplesPerPixel; k++)
        {
            var dst = raw[firstChannel + k];
            int d = firstPixel * bps;
            for (int p = 0, o = offset + k * bps; p < pixels; p++, o += samplesPerPixel * bps, d += bps)
                Buffer.BlockCopy(src, o, dst, d, bps);
        }
    }

    private static float[] Decode(byte[] raw, SampleFormat format, int bits)
    {
        float[] plane = new float[raw.Length / (bits / 8)];
        switch (format, bits)
        {
            case (SampleFormat.UINT, 8): for (int i = 0; i < plane.Length; i++) plane[i] = raw[i]; break;
            case (SampleFormat.INT, 8): for (int i = 0; i < plane.Length; i++) plane[i] = (sbyte)raw[i]; break;
            case (SampleFormat.UINT, 16): Convert(MemoryMarshal.Cast<byte, ushort>(raw), plane, v => v); break;
            case (SampleFormat.INT, 16): Convert(MemoryMarshal.Cast<byte, short>(raw), plane, v => v); break;
            case (SampleFormat.UINT, 32): Convert(MemoryMarshal.Cast<byte, uint>(raw), plane, v => v); break;
            case (SampleFormat.INT, 32): Convert(MemoryMarshal.Cast<byte, int>(raw), plane, v => v); break;
            case (_, 16): Convert(MemoryMarshal.Cast<byte, Half>(raw), plane, v => (float)v); break;
            case (_, 32): Convert(MemoryMarshal.Cast<byte, float>(raw), plane, v => v); break;
            default: Convert(MemoryMarshal.Cast<byte, double>(raw), plane, v => (float)v); break;
        }
        for (int i = 0; i < plane.Length; i++)
            if (!float.IsFinite(plane[i])) plane[i] = 0f;
        return plane;
    }

    private static void Convert<T>(ReadOnlySpan<T> src, float[] dst, Func<T, float> convert)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = convert(src[i]);
    }

    private static int Int(LibTiff tif, TiffTag tag, int? fallback = null) =>
        tif.GetField(tag) is { Length: > 0 } values ? values[0].ToInt()
        : fallback ?? throw new InvalidDataException($"TIFF file is missing {tag}.");

    // ----------------------------------------------------------------- saving

    /// <summary>
    /// Writes the file, replacing the image's pixels with <paramref name="planes"/> (or the current
    /// <see cref="Planes"/>) turned by <paramref name="rotation"/>. The sample format, planar layout and
    /// compression are kept; images are always written in strips.
    /// </summary>
    public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None)
    {
        planes ??= Planes;
        if (planes.Length != Channels || planes.Any(p => p.Length != PixelCount))
            throw new ArgumentException("Plane data does not match the image geometry.", nameof(planes));
        planes = rotation.Apply(planes, Width, Height);
        var (width, height) = rotation.Size(Width, Height);

        var raw = new byte[Channels][];
        Parallel.For(0, Channels, c => raw[c] = Encode(planes[c]));

        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + ".tmp";
        bool bigTiff = (long)PixelCount * Channels * BytesPerSample > 3_000_000_000L;
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20))
        using (var tif = LibTiff.ClientOpen(tempPath, bigTiff ? "w8" : "w", fs, new TiffStream()) ?? throw new IOException($"Could not create {tempPath}."))
        {
            var compression = WritableCompressions.Contains(Compression) ? Compression : Compression.LZW;
            bool separate = _planarConfig == PlanarConfig.SEPARATE;

            tif.SetField(TiffTag.IMAGEWIDTH, width);
            tif.SetField(TiffTag.IMAGELENGTH, height);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, Channels);
            tif.SetField(TiffTag.BITSPERSAMPLE, BitsPerSample);
            tif.SetField(TiffTag.SAMPLEFORMAT, SampleFormat);
            tif.SetField(TiffTag.PHOTOMETRIC, _photometric);
            tif.SetField(TiffTag.PLANARCONFIG, _planarConfig);
            tif.SetField(TiffTag.COMPRESSION, compression);
            if (compression is Compression.LZW or Compression.DEFLATE or Compression.ADOBE_DEFLATE && _predictor > 1)
                tif.SetField(TiffTag.PREDICTOR, _predictor);
            foreach (var (tag, values) in _metadata)
            {
                // A quarter turn swaps the horizontal and vertical resolutions.
                var target = !rotation.SwapsAxes() ? tag
                    : tag == TiffTag.XRESOLUTION ? TiffTag.YRESOLUTION
                    : tag == TiffTag.YRESOLUTION ? TiffTag.XRESOLUTION
                    : tag;
                tif.SetField(target, values);
            }

            int samplesPerPixel = separate ? 1 : Channels;
            int lineBytes = width * samplesPerPixel * BytesPerSample;
            tif.SetField(TiffTag.ROWSPERSTRIP, Math.Max(1, (1 << 20) / lineBytes));

            var line = new byte[lineBytes];
            for (int s = 0; s < (separate ? Channels : 1); s++)
                for (int y = 0; y < height; y++)
                {
                    Gather(raw, s, samplesPerPixel, y * width, width, line);
                    if (!tif.WriteScanline(line, y, (short)s))
                        throw new IOException("Could not write the image data.");
                }
        }
        File.Move(tempPath, fullPath, overwrite: true);
    }

    /// <summary>Interleaves one row of the per-channel arrays into <paramref name="line"/>.</summary>
    private void Gather(byte[][] raw, int firstChannel, int samplesPerPixel, int firstPixel, int width, byte[] line)
    {
        int bps = BytesPerSample;
        for (int k = 0; k < samplesPerPixel; k++)
        {
            var src = raw[firstChannel + k];
            for (int p = 0, o = k * bps, d = firstPixel * bps; p < width; p++, o += samplesPerPixel * bps, d += bps)
                Buffer.BlockCopy(src, d, line, o, bps);
        }
    }

    private byte[] Encode(float[] plane)
    {
        var raw = new byte[(long)plane.Length * BytesPerSample];
        double range = _high - _low;
        bool invert = _photometric == Photometric.MINISWHITE;

        double Stored(int i) => ((invert ? 1 - plane[i] : plane[i]) * range) + _low;
        double Rounded(int i, double min, double max) => Math.Clamp(Math.Round(Stored(i)), min, max);

        switch (SampleFormat, BitsPerSample)
        {
            case (SampleFormat.UINT, 8): for (int i = 0; i < plane.Length; i++) raw[i] = (byte)Rounded(i, 0, byte.MaxValue); break;
            case (SampleFormat.INT, 8): for (int i = 0; i < plane.Length; i++) raw[i] = (byte)(sbyte)Rounded(i, sbyte.MinValue, sbyte.MaxValue); break;
            case (SampleFormat.UINT, 16): Fill(MemoryMarshal.Cast<byte, ushort>(raw.AsSpan()), i => (ushort)Rounded(i, 0, ushort.MaxValue)); break;
            case (SampleFormat.INT, 16): Fill(MemoryMarshal.Cast<byte, short>(raw.AsSpan()), i => (short)Rounded(i, short.MinValue, short.MaxValue)); break;
            case (SampleFormat.UINT, 32): Fill(MemoryMarshal.Cast<byte, uint>(raw.AsSpan()), i => (uint)Rounded(i, 0, uint.MaxValue)); break;
            case (SampleFormat.INT, 32): Fill(MemoryMarshal.Cast<byte, int>(raw.AsSpan()), i => (int)Rounded(i, int.MinValue, int.MaxValue)); break;
            case (_, 16): Fill(MemoryMarshal.Cast<byte, Half>(raw.AsSpan()), i => (Half)Stored(i)); break;
            case (_, 32): Fill(MemoryMarshal.Cast<byte, float>(raw.AsSpan()), i => (float)Stored(i)); break;
            default: Fill(MemoryMarshal.Cast<byte, double>(raw.AsSpan()), Stored); break;
        }
        return raw;
    }

    private static void Fill<T>(Span<T> dst, Func<int, T> value)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = value(i);
    }

    /// <summary>Turns LibTiff errors into exceptions instead of console output; warnings are ignored.</summary>
    private sealed class ThrowingErrorHandler : TiffErrorHandler
    {
        public override void ErrorHandler(LibTiff tif, string method, string format, params object[] args) =>
            throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, format, args));

        public override void ErrorHandlerExt(LibTiff tif, object clientData, string method, string format, params object[] args) { }

        public override void WarningHandler(LibTiff tif, string method, string format, params object[] args) { }

        public override void WarningHandlerExt(LibTiff tif, object clientData, string method, string format, params object[] args) { }
    }
}
