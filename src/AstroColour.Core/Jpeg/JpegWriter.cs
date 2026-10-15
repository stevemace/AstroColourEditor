using System.Runtime.InteropServices;
using SkiaSharp;

namespace AstroColour.Core.Jpeg;

/// <summary>Writes greyscale (one plane) or RGB (three planes) 8-bit JPEG files.</summary>
public static class JpegWriter
{
    /// <summary>
    /// Writes <paramref name="planes"/>, clamped to [0,1], as a JPEG file. Colour is kept at full resolution
    /// (no chroma subsampling) so that small, coloured stars keep their colour.
    /// </summary>
    public static void Save(string path, int width, int height, float[][] planes, int quality = 95)
    {
        bool colour = planes.Length >= 3;
        int pixelBytes = colour ? 4 : 1;
        var pixels = new byte[(long)width * height * pixelBytes];
        Parallel.For(0, height, y =>
        {
            int start = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = start + x;
                if (colour)
                {
                    pixels[i * 4] = ToByte(planes[0][i]);
                    pixels[i * 4 + 1] = ToByte(planes[1][i]);
                    pixels[i * 4 + 2] = ToByte(planes[2][i]);
                    pixels[i * 4 + 3] = 255;
                }
                else
                {
                    pixels[i] = ToByte(planes[0][i]);
                }
            }
        });

        var info = new SKImageInfo(width, height, colour ? SKColorType.Rgba8888 : SKColorType.Gray8, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var pixmap = new SKPixmap(info, handle.AddrOfPinnedObject(), width * pixelBytes);
            using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore))
                ?? throw new IOException("Could not encode the JPEG image.");

            string fullPath = Path.GetFullPath(path);
            string tempPath = fullPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                data.SaveTo(fs);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            handle.Free();
        }
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
}
