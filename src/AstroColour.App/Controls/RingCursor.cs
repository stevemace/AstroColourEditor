using System.Runtime.InteropServices;
using Microsoft.UI.Input;

namespace AstroColour.App.Controls;

/// <summary>
/// A small ring cursor for the colour picker: a dark ring with a thin white ring inside it,
/// visible on both dark sky and bright nebulae. It is drawn at runtime as a Win32 cursor and
/// handed to WinUI through IInputCursorStaticsInterop.
/// </summary>
internal static class RingCursor
{
    private const double DiameterDips = 17;

    // Cursors by pixel size. The HCURSORs are kept alive for the life of the app, as the
    // InputCursor wrapping them does not take ownership.
    private static readonly Dictionary<int, InputCursor> Cache = [];

    public static InputCursor Get(double rasterizationScale)
    {
        int size = Math.Max((int)Math.Round(DiameterDips * rasterizationScale), 9) | 1; // odd: hotspot is the centre pixel
        if (!Cache.TryGetValue(size, out var cursor))
        {
            cursor = FromHCursor(CreateRing(size));
            Cache[size] = cursor;
        }
        return cursor;
    }

    private static IntPtr CreateRing(int size)
    {
        double scale = size / DiameterDips;
        double centre = (size - 1) / 2.0;
        double outer = size / 2.0;
        double dark = 3.4 * scale;           // width of the dark ring
        double whiteMid = outer - dark / 2;  // white ring runs along the middle of the dark one
        double whiteHalf = 0.7 * scale;

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
                int grey = (int)Math.Round(255 * aw / a);
                int alpha = (int)Math.Round(255 * a);
                pixels[y * size + x] = alpha << 24 | grey << 16 | grey << 8 | grey;
            }
        }

        var header = new BITMAPV5HEADER
        {
            bV5Size = Marshal.SizeOf<BITMAPV5HEADER>(),
            bV5Width = size,
            bV5Height = -size, // top-down
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = 3, // BI_BITFIELDS
            bV5RedMask = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
        };
        IntPtr colour = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        IntPtr mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);

        var info = new ICONINFO { fIcon = false, xHotspot = size / 2, yHotspot = size / 2, hbmMask = mask, hbmColor = colour };
        IntPtr cursor = CreateIconIndirect(ref info);
        DeleteObject(colour);
        DeleteObject(mask);
        if (cursor == IntPtr.Zero) throw new InvalidOperationException("Could not create the picker cursor.");
        return cursor;
    }

    /// <summary>Anti-aliased coverage of a pixel at distance d by the band [inner, outer].</summary>
    private static double Coverage(double d, double inner, double outer) =>
        Math.Clamp(Math.Min(d - inner, outer - d) + 0.5, 0, 1);

    /// <summary>Wraps an HCURSOR as a WinUI InputCursor (see Microsoft.UI.Input.InputCursor.Interop.h).</summary>
    private static unsafe InputCursor FromHCursor(IntPtr hcursor)
    {
        var factory = WinRT.ActivationFactory.Get("Microsoft.UI.Input.InputCursor");
        Guid iid = new("ac6f5065-90c4-46ce-beb7-05e138e54117"); // IInputCursorStaticsInterop
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in iid, out IntPtr interop));
        try
        {
            // Slot 6: first method after IUnknown (3) and IInspectable (3).
            var createFromHCursor = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[6];
            IntPtr result;
            Marshal.ThrowExceptionForHR(createFromHCursor(interop, hcursor, &result));
            try
            {
                return InputCursor.FromAbi(result);
            }
            finally
            {
                Marshal.Release(result);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public int bV5Size;
        public int bV5Width;
        public int bV5Height;
        public short bV5Planes;
        public short bV5BitCount;
        public int bV5Compression;
        public int bV5SizeImage;
        public int bV5XPelsPerMeter;
        public int bV5YPelsPerMeter;
        public int bV5ClrUsed;
        public int bV5ClrImportant;
        public uint bV5RedMask;
        public uint bV5GreenMask;
        public uint bV5BlueMask;
        public uint bV5AlphaMask;
        public int bV5CSType;
        public int ciexyzRedX, ciexyzRedY, ciexyzRedZ;
        public int ciexyzGreenX, ciexyzGreenY, ciexyzGreenZ;
        public int ciexyzBlueX, ciexyzBlueY, ciexyzBlueZ;
        public int bV5GammaRed;
        public int bV5GammaGreen;
        public int bV5GammaBlue;
        public int bV5Intent;
        public int bV5ProfileData;
        public int bV5ProfileSize;
        public int bV5Reserved;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPV5HEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
}
