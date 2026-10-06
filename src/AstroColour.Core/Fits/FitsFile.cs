using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace AstroColour.Core.Fits;

/// <summary>
/// A FITS file. The first HDU holding a two-dimensional image (primary or IMAGE extension; mono, or
/// colour as three planes along NAXIS3) is decoded into normalised [0,1] float planes. Every header
/// card and every other HDU is preserved verbatim when the file is saved.
/// </summary>
public sealed class FitsFile : IAstroImage
{
    private const int BlockSize = 2880;
    private const int CardSize = 80;

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public int Bitpix { get; }
    public string ColorSpace => Channels == 3 ? "RGB" : "Gray";
    public string SampleFormatName => Bitpix switch
    {
        8 => "UInt8",
        16 => _unsignedInteger ? "UInt16" : "Int16",
        32 => _unsignedInteger ? "UInt32" : "Int32",
        -32 => "Float32",
        _ => "Float64",
    };

    public float[][] Planes { get; }
    public bool IsColour => Channels >= 3;
    public int PixelCount => Width * Height;

    /// <summary>True when the file stores its first row at the bottom; planes are kept top row first.</summary>
    public bool BottomUp { get; }

    // Everything before the image HDU's header, and everything after its data, kept byte for byte.
    private readonly byte[] _before;
    private readonly byte[] _after;
    private readonly List<string> _cards;

    // Physical value = BZERO + BSCALE × stored value; normalised = (physical − _low) / (_high − _low).
    private readonly double _bzero, _bscale, _low, _high;
    private readonly bool _unsignedInteger;

    private FitsFile(byte[] before, byte[] after, List<string> cards, int width, int height, int channels, int bitpix,
        double bzero, double bscale, double low, double high, bool unsignedInteger, bool bottomUp, float[][] planes)
    {
        _before = before;
        _after = after;
        _cards = cards;
        Width = width;
        Height = height;
        Channels = channels;
        Bitpix = bitpix;
        _bzero = bzero;
        _bscale = bscale;
        _low = low;
        _high = high;
        _unsignedInteger = unsignedInteger;
        BottomUp = bottomUp;
        Planes = planes;
    }

    // ---------------------------------------------------------------- loading

    public static FitsFile Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Load(fs);
    }

    public static FitsFile Load(Stream fs)
    {
        bool sawCompressedImage = false;
        long hduStart = 0;
        while (hduStart < fs.Length)
        {
            fs.Seek(hduStart, SeekOrigin.Begin);
            var cards = ReadHeader(fs, primary: hduStart == 0);
            long dataStart = fs.Position;
            var header = new Header(cards);

            int bitpix = header.Int("BITPIX");
            int naxis = header.Int("NAXIS");
            var axes = Enumerable.Range(1, naxis).Select(i => (long)header.Int($"NAXIS{i}")).ToArray();
            long elements = naxis == 0 ? 0 : axes.Aggregate(1L, (a, b) => a * b);
            long dataSize = Math.Abs(bitpix) / 8 * header.Long("GCOUNT", 1) * (header.Long("PCOUNT", 0) + elements);
            long nextHdu = dataStart + Align(dataSize);

            string? extension = header.String("XTENSION")?.Trim();
            bool isImageHdu = hduStart == 0 || string.Equals(extension, "IMAGE", StringComparison.OrdinalIgnoreCase);
            if (header.Bool("ZIMAGE")) sawCompressedImage = true;

            if (isImageHdu && naxis >= 2 && axes[0] > 0 && axes[1] > 0)
            {
                if (axes.Skip(3).Any(a => a != 1))
                    throw new NotSupportedException("Only two-dimensional images (optionally with colour planes) are supported.");
                if (dataStart + dataSize > fs.Length)
                    throw new InvalidDataException("Image data is truncated.");
                return Decode(fs, hduStart, dataStart, nextHdu, cards, header, bitpix,
                    (int)axes[0], (int)axes[1], naxis >= 3 ? (int)axes[2] : 1);
            }

            hduStart = nextHdu;
        }

        throw sawCompressedImage
            ? new NotSupportedException("Compressed (fpack) FITS images are not supported. Decompress the file with funpack first.")
            : new InvalidDataException("The file does not contain an image.");
    }

    private static FitsFile Decode(Stream fs, long hduStart, long dataStart, long nextHdu, List<string> cards, Header header,
        int bitpix, int width, int height, int channels)
    {
        if (bitpix is not (8 or 16 or 32 or -32 or -64))
            throw new NotSupportedException($"Unsupported FITS sample format (BITPIX = {bitpix}).");
        if (channels < 1 || (long)width * height * channels * Math.Abs(bitpix / 8) > Array.MaxLength)
            throw new NotSupportedException("The image is too large.");

        double bzero = header.Double("BZERO", 0);
        double bscale = header.Double("BSCALE", 1);
        if (bscale == 0) bscale = 1;
        bool bottomUp = string.Equals(header.String("ROWORDER")?.Trim(), "BOTTOM-UP", StringComparison.OrdinalIgnoreCase);

        int n = width * height;
        int bps = Math.Abs(bitpix) / 8;
        var data = new byte[(long)n * channels * bps];
        fs.Seek(dataStart, SeekOrigin.Begin);
        fs.ReadExactly(data);

        // Physical values, top row first.
        var planes = new float[channels][];
        Parallel.For(0, channels, c =>
        {
            var plane = new float[n];
            ReadOnlySpan<byte> bytes = data.AsSpan(c * n * bps, n * bps);
            for (int y = 0; y < height; y++)
            {
                int srcRow = (bottomUp ? height - 1 - y : y) * width;
                int dstRow = y * width;
                for (int x = 0; x < width; x++)
                {
                    var s = bytes.Slice((srcRow + x) * bps, bps);
                    double raw = bitpix switch
                    {
                        8 => s[0],
                        16 => BinaryPrimitives.ReadInt16BigEndian(s),
                        32 => BinaryPrimitives.ReadInt32BigEndian(s),
                        -32 => BinaryPrimitives.ReadSingleBigEndian(s),
                        _ => BinaryPrimitives.ReadDoubleBigEndian(s),
                    };
                    double v = bzero + bscale * raw;
                    plane[dstRow + x] = double.IsFinite(v) ? (float)v : 0f;
                }
            }
            planes[c] = plane;
        });

        // Integers use their type's range: unsigned when BZERO is the standard offset (2^(bits-1)).
        // Floats are usually [0,1], sometimes [0,65535].
        double low = 0, high;
        bool unsignedInteger = false;
        if (bitpix > 0)
        {
            double offset = bitpix == 8 ? 0 : Math.Pow(2, bitpix - 1);
            unsignedInteger = bitpix > 8 && bscale == 1 && bzero == offset;
            high = bitpix == 8 ? 255 : unsignedInteger ? Math.Pow(2, bitpix) - 1 : offset - 1;
        }
        else
        {
            float max = planes.Max(p => p.Length == 0 ? 0f : p.Max());
            high = max <= 1.5f ? 1 : max <= 65535 * 1.01f ? 65535 : max;
        }

        float scale = (float)(1 / (high - low));
        Parallel.For(0, channels, c =>
        {
            var plane = planes[c];
            for (int i = 0; i < plane.Length; i++) plane[i] = (float)((plane[i] - low) * scale);
        });

        var before = new byte[hduStart];
        fs.Seek(0, SeekOrigin.Begin);
        fs.ReadExactly(before);
        var after = new byte[Math.Max(0, fs.Length - nextHdu)];
        if (after.Length > 0)
        {
            fs.Seek(nextHdu, SeekOrigin.Begin);
            fs.ReadExactly(after);
        }

        return new FitsFile(before, after, cards, width, height, channels, bitpix,
            bzero, bscale, low, high, unsignedInteger, bottomUp, planes);
    }

    /// <summary>Reads 2880-byte header blocks up to and including the one with the END card.</summary>
    private static List<string> ReadHeader(Stream fs, bool primary)
    {
        var cards = new List<string>();
        var block = new byte[BlockSize];
        while (true)
        {
            if (fs.Length - fs.Position < BlockSize)
                throw new InvalidDataException(primary && cards.Count == 0 ? "Not a FITS file." : "FITS header is truncated.");
            fs.ReadExactly(block);
            for (int i = 0; i < BlockSize; i += CardSize)
            {
                string card = Encoding.ASCII.GetString(block, i, CardSize);
                if (cards.Count == 0 && primary && !card.StartsWith("SIMPLE  =", StringComparison.Ordinal))
                    throw new InvalidDataException("Not a FITS file (missing SIMPLE keyword).");
                if (card.StartsWith("END", StringComparison.Ordinal) && card[3..].Trim().Length == 0)
                    return cards;
                cards.Add(card);
            }
        }
    }

    private static long Align(long size) => (size + BlockSize - 1) / BlockSize * BlockSize;

    /// <summary>Keyword lookup over header cards (first occurrence wins).</summary>
    private sealed class Header
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Header(IEnumerable<string> cards)
        {
            foreach (var card in cards)
            {
                if (card.Length < 10 || card[8] != '=' || card[9] != ' ') continue;
                _values.TryAdd(card[..8].TrimEnd(), ParseValue(card[10..]));
            }
        }

        public string? String(string key) => _values.GetValueOrDefault(key);

        public int Int(string key) => (int)Long(key, null);

        public long Long(string key, long? fallback = null) =>
            _values.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : fallback ?? throw new InvalidDataException($"FITS header is missing {key}.");

        public double Double(string key, double fallback) =>
            _values.TryGetValue(key, out var v) &&
            double.TryParse(v.Replace('D', 'E'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

        public bool Bool(string key) => _values.TryGetValue(key, out var v) && v == "T";

        /// <summary>The value field without its comment; quoted strings are unquoted.</summary>
        private static string ParseValue(string field)
        {
            field = field.TrimStart();
            if (field.StartsWith('\''))
            {
                var sb = new StringBuilder();
                for (int i = 1; i < field.Length; i++)
                {
                    if (field[i] != '\'') { sb.Append(field[i]); continue; }
                    if (i + 1 < field.Length && field[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                    break;
                }
                return sb.ToString().TrimEnd();
            }
            int slash = field.IndexOf('/');
            return (slash >= 0 ? field[..slash] : field).Trim();
        }
    }

    // ----------------------------------------------------------------- saving

    /// <summary>
    /// Writes the file, replacing the image's pixels with <paramref name="planes"/> (or the current
    /// <see cref="Planes"/>). The sample format, scaling and row order are kept; checksums, which
    /// would no longer match, are removed.
    /// </summary>
    public void Save(string path, float[][]? planes = null)
    {
        planes ??= Planes;
        if (planes.Length != Channels || planes.Any(p => p.Length != PixelCount))
            throw new ArgumentException("Plane data does not match the image geometry.", nameof(planes));

        var header = new StringBuilder();
        foreach (var card in _cards)
        {
            string key = card[..8].TrimEnd();
            if (key is "CHECKSUM" or "DATASUM") continue;
            header.Append(card);
        }
        header.Append("END".PadRight(CardSize));
        while (header.Length % BlockSize != 0) header.Append(' ');

        byte[] data = EncodeSamples(planes);

        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            fs.Write(_before);
            fs.Write(Encoding.ASCII.GetBytes(header.ToString()));
            fs.Write(data);
            fs.Write(new byte[Align(data.LongLength) - data.LongLength]);
            fs.Write(_after);
        }
        File.Move(tempPath, fullPath, overwrite: true);
    }

    private byte[] EncodeSamples(float[][] planes)
    {
        int n = PixelCount;
        int bps = Math.Abs(Bitpix) / 8;
        var data = new byte[(long)n * Channels * bps];
        double range = _high - _low;

        Parallel.For(0, Channels, c =>
        {
            var plane = planes[c];
            var bytes = new Span<byte>(data, c * n * bps, n * bps);
            for (int y = 0; y < Height; y++)
            {
                int srcRow = y * Width;
                int dstRow = (BottomUp ? Height - 1 - y : y) * Width;
                for (int x = 0; x < Width; x++)
                {
                    var s = bytes.Slice((dstRow + x) * bps, bps);
                    double stored = (plane[srcRow + x] * range + _low - _bzero) / _bscale;
                    switch (Bitpix)
                    {
                        case 8:
                            s[0] = (byte)Math.Clamp(Math.Round(stored), 0, 255);
                            break;
                        case 16:
                            BinaryPrimitives.WriteInt16BigEndian(s, (short)Math.Clamp(Math.Round(stored), short.MinValue, short.MaxValue));
                            break;
                        case 32:
                            BinaryPrimitives.WriteInt32BigEndian(s, (int)Math.Clamp(Math.Round(stored), int.MinValue, int.MaxValue));
                            break;
                        case -32:
                            BinaryPrimitives.WriteSingleBigEndian(s, (float)stored);
                            break;
                        default:
                            BinaryPrimitives.WriteDoubleBigEndian(s, stored);
                            break;
                    }
                }
            }
        });
        return data;
    }
}
