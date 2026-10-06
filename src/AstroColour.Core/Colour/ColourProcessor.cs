using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AstroColour.Core.Colour;

public enum PreviewMode
{
    /// <summary>The image with all adjustments applied.</summary>
    Adjusted,
    /// <summary>The unedited image, for before/after comparison.</summary>
    Original,
    /// <summary>The adjusted image where the mask adjustment selects it, greyscale elsewhere.</summary>
    Mask,
}

public static class ColourProcessor
{
    /// <summary>Renders the image into an 8-bit BGRA buffer of Width×Height×4 bytes.</summary>
    public static void RenderPreview(RgbImage image, IReadOnlyList<ColourAdjustment> adjustments,
        PreviewMode mode, ColourAdjustment? mask, byte[] bgra, CancellationToken cancellation = default)
    {
        var set = new AdjustmentSet(adjustments);
        var maskSet = mode == PreviewMode.Mask && mask is not null ? new AdjustmentSet([mask], includeAll: true) : null;
        if (mode == PreviewMode.Original) set = new AdjustmentSet([]);

        float[] rp = image.R, gp = image.G, bp = image.B;
        int width = image.Width;
        var options = new ParallelOptions { CancellationToken = cancellation };

        Parallel.For(0, image.Height, options, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            var output = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
            int row = y * width;
            for (int i = row; i < row + width; i++)
            {
                float r = rp[i], g = gp[i], b = bp[i];

                if (maskSet is not null)
                {
                    float cr = Math.Clamp(r, 0f, 1f), cg = Math.Clamp(g, 0f, 1f), cb = Math.Clamp(b, 0f, 1f);
                    set.Adjust(ref r, ref g, ref b);
                    ColourMath.RgbToHsl(cr, cg, cb, out float h, out _, out _, out float chroma);
                    float w = maskSet.Weight(h, chroma);
                    float grey = 0.2126f * cr + 0.7152f * cg + 0.0722f * cb;
                    r = grey + (r - grey) * w;
                    g = grey + (g - grey) * w;
                    b = grey + (b - grey) * w;
                }
                else
                {
                    set.Adjust(ref r, ref g, ref b);
                }

                output[i] = 0xFF000000u | (uint)ToByte(r) << 16 | (uint)ToByte(g) << 8 | ToByte(b);
            }
        });
    }

    /// <summary>Applies the adjustments at full precision, returning new planes for saving.</summary>
    public static float[][] Apply(IAstroImage image, IReadOnlyList<ColourAdjustment> adjustments)
    {
        var planes = image.Planes.Select(p => (float[])p.Clone()).ToArray();
        if (!image.IsColour) return planes;

        var set = new AdjustmentSet(adjustments);
        if (set.IsEmpty) return planes;

        float[] rp = planes[0], gp = planes[1], bp = planes[2];
        int width = image.Width;
        Parallel.For(0, image.Height, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            int row = y * width;
            for (int i = row; i < row + width; i++)
            {
                float r = rp[i], g = gp[i], b = bp[i];
                if (set.Adjust(ref r, ref g, ref b))
                {
                    rp[i] = r;
                    gp[i] = g;
                    bp[i] = b;
                }
            }
        });
        return planes;
    }

    /// <summary>Average colour in a (2·radius+1)² window around (x, y), clipped to the image.</summary>
    public static (float R, float G, float B) Sample(RgbImage image, int x, int y, int radius = 2)
    {
        double r = 0, g = 0, b = 0;
        int count = 0;
        for (int yy = Math.Max(0, y - radius); yy <= Math.Min(image.Height - 1, y + radius); yy++)
        {
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(image.Width - 1, x + radius); xx++)
            {
                int i = yy * image.Width + xx;
                r += Math.Clamp(image.R[i], 0f, 1f);
                g += Math.Clamp(image.G[i], 0f, 1f);
                b += Math.Clamp(image.B[i], 0f, 1f);
                count++;
            }
        }
        return count == 0 ? (0, 0, 0) : ((float)(r / count), (float)(g / count), (float)(b / count));
    }

    /// <summary>Median of a sparse sample of the luminance, used to spot unstretched (linear) data.</summary>
    public static float EstimateMedian(RgbImage image)
    {
        int step = Math.Max(1, image.PixelCount / 100_000);
        var samples = new List<float>();
        for (int i = 0; i < image.PixelCount; i += step)
            samples.Add((image.R[i] + image.G[i] + image.B[i]) / 3f);
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
}
