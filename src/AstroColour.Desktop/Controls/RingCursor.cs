using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AstroColour.Desktop.Controls;

/// <summary>
/// A small ring cursor for the colour picker: a dark ring with a thin white ring inside it,
/// visible on both dark sky and bright nebulae.
/// </summary>
internal static class RingCursor
{
    private const double DiameterDips = 17;

    private static readonly Dictionary<int, Cursor> Cache = [];

    public static Cursor Get(double renderScaling)
    {
        // macOS takes the cursor bitmap's size in points, so drawing it larger would only make it bigger.
        if (OperatingSystem.IsMacOS()) renderScaling = 1;
        int size = Math.Max((int)Math.Round(DiameterDips * renderScaling), 9) | 1; // odd: hotspot is the centre pixel
        if (!Cache.TryGetValue(size, out var cursor))
        {
            cursor = new Cursor(CreateRing(size), new PixelPoint(size / 2, size / 2));
            Cache[size] = cursor;
        }
        return cursor;
    }

    private static Bitmap CreateRing(int size)
    {
        double scale = size / DiameterDips;
        double centre = (size - 1) / 2.0;
        double outer = size / 2.0;
        double dark = 3.4 * scale;           // width of the dark ring
        double whiteMid = outer - dark / 2;  // white ring runs along the middle of the dark one
        double whiteHalf = 0.7 * scale;

        // Premultiplied BGRA.
        var pixels = new int[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double d = Math.Sqrt((x - centre) * (x - centre) + (y - centre) * (y - centre));
                double ad = 0.7 * Coverage(d, outer - dark, outer);
                double aw = Coverage(d, whiteMid - whiteHalf, whiteMid + whiteHalf);
                double a = aw + ad * (1 - aw);
                if (a <= 0) continue;
                int grey = (int)Math.Round(255 * aw);
                int alpha = (int)Math.Round(255 * a);
                pixels[y * size + x] = alpha << 24 | grey << 16 | grey << 8 | grey;
            }
        }

        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = bitmap.Lock())
        {
            for (int y = 0; y < size; y++)
                Marshal.Copy(pixels, y * size, buffer.Address + y * buffer.RowBytes, size);
        }
        return bitmap;
    }

    /// <summary>Anti-aliased coverage of a pixel at distance d by the band [inner, outer].</summary>
    private static double Coverage(double d, double inner, double outer) =>
        Math.Clamp(Math.Min(d - inner, outer - d) + 0.5, 0, 1);
}
