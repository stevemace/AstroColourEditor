using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AstroColour.Core.Png;

/// <summary>Writes greyscale (one plane) or RGB (three planes) PNG files, 8 or 16 bits per sample.</summary>
public static class PngWriter
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaxChunk = 1 << 20;

    /// <summary>Writes <paramref name="planes"/>, clamped to [0,1], as a PNG file.</summary>
    public static void Save(string path, int width, int height, float[][] planes, bool sixteenBit)
    {
        int channels = planes.Length >= 3 ? 3 : 1;
        int bps = sixteenBit ? 2 : 1;
        int pixelBytes = channels * bps;
        int rowBytes = width * pixelBytes;

        // Each row is filtered with whichever PNG filter leaves the smallest residuals, which compresses best.
        var rows = new byte[height][];
        Parallel.For(0, height, y =>
        {
            var current = Row(planes, y, width, channels, sixteenBit);
            var previous = y > 0 ? Row(planes, y - 1, width, channels, sixteenBit) : new byte[rowBytes];
            rows[y] = Filter(current, previous, pixelBytes);
        });

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                foreach (var row in rows) zlib.Write(row);
            compressed = ms.ToArray();
        }

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)(bps * 8);
        header[9] = (byte)(channels == 3 ? 2 : 0); // truecolour or greyscale
        // Compression, filter and interlace methods are all 0.

        string fullPath = Path.GetFullPath(path);
        string tempPath = fullPath + ".tmp";
        using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            fs.Write(Signature);
            WriteChunk(fs, "IHDR", header);
            WriteChunk(fs, "tEXt", Encoding.Latin1.GetBytes("Software\0Astro Colour Editor"));
            for (int offset = 0; offset < compressed.Length || offset == 0; offset += MaxChunk)
                WriteChunk(fs, "IDAT", compressed.AsSpan(offset, Math.Min(MaxChunk, compressed.Length - offset)));
            WriteChunk(fs, "IEND", []);
        }
        File.Move(tempPath, fullPath, overwrite: true);
    }

    /// <summary>One row of interleaved samples, big-endian.</summary>
    private static byte[] Row(float[][] planes, int y, int width, int channels, bool sixteenBit)
    {
        var row = new byte[width * channels * (sixteenBit ? 2 : 1)];
        int start = y * width;
        for (int x = 0, o = 0; x < width; x++)
            for (int c = 0; c < channels; c++)
            {
                float v = Math.Clamp(planes[c][start + x], 0f, 1f);
                if (sixteenBit)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(o), (ushort)(v * 65535f + 0.5f));
                    o += 2;
                }
                else
                {
                    row[o++] = (byte)(v * 255f + 0.5f);
                }
            }
        return row;
    }

    /// <summary>The filter type byte followed by the filtered row, using the filter with the smallest sum of absolute residuals.</summary>
    private static byte[] Filter(byte[] row, byte[] previous, int pixelBytes)
    {
        byte[]? best = null;
        long bestScore = long.MaxValue;
        for (byte type = 0; type <= 4; type++)
        {
            var filtered = new byte[row.Length + 1];
            filtered[0] = type;
            long score = 0;
            for (int i = 0; i < row.Length; i++)
            {
                int a = i >= pixelBytes ? row[i - pixelBytes] : 0;
                int b = previous[i];
                int c = i >= pixelBytes ? previous[i - pixelBytes] : 0;
                int predicted = type switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => Paeth(a, b, c),
                };
                byte residual = (byte)(row[i] - predicted);
                filtered[i + 1] = residual;
                score += residual < 128 ? residual : 256 - residual;
            }
            if (score < bestScore)
            {
                bestScore = score;
                best = filtered;
            }
        }
        return best!;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        s.Write(buffer);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        s.Write(buffer);
    }

    private static class Crc32
    {
        private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
