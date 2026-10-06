using System.Runtime.CompilerServices;

namespace AstroColour.Core.Colour;

/// <summary>Three colour planes (shared with the source where possible) that the colour engine works on.</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, float[] r, float[] g, float[] b)
    {
        Width = width;
        Height = height;
        R = r;
        G = g;
        B = b;
    }

    public int Width { get; }
    public int Height { get; }
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }
    public int PixelCount => Width * Height;

    /// <summary>Wraps the image's planes without copying; a monochrome image uses one plane for all three.</summary>
    public static RgbImage From(IAstroImage image)
    {
        var r = image.Planes[0];
        return image.IsColour
            ? new RgbImage(image.Width, image.Height, r, image.Planes[1], image.Planes[2])
            : new RgbImage(image.Width, image.Height, r, r, r);
    }

    /// <summary>Box-filters the image down by an integer factor.</summary>
    public RgbImage Downsample(int factor)
    {
        if (factor <= 1) return this;
        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var r = new float[w * h];
        var g = new float[w * h];
        var b = new float[w * h];
        float scale = 1f / (factor * factor);

        Parallel.For(0, h, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            for (int x = 0; x < w; x++)
            {
                float sr = 0, sg = 0, sb = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y * factor + dy) * Width + x * factor;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sr += R[row + dx];
                        sg += G[row + dx];
                        sb += B[row + dx];
                    }
                }
                int o = y * w + x;
                r[o] = sr * scale;
                g[o] = sg * scale;
                b[o] = sb * scale;
            }
        });
        return new RgbImage(w, h, r, g, b);
    }
}
