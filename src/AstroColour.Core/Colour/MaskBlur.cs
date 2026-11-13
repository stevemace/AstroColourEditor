using System.Runtime.CompilerServices;

namespace AstroColour.Core.Colour;

/// <summary>
/// A fast approximate Gaussian blur of a single-channel plane: three box blurs in each direction, whose cost does not
/// depend on the radius. Edges are extended by repeating the outermost pixels.
/// </summary>
public static class MaskBlur
{
    private const int Passes = 3;
    private const int ColumnStrip = 64; // columns blurred together in the vertical pass, for cache-friendly row access

    /// <summary>Blurs <paramref name="plane"/> in place with a Gaussian of roughly the given standard deviation in pixels.</summary>
    public static void Gaussian(float[] plane, int width, int height, float sigma, CancellationToken cancellation = default)
    {
        if (sigma < 0.5f || width <= 0 || height <= 0) return;
        var options = new ParallelOptions { CancellationToken = cancellation };
        var scratch = new float[plane.Length];
        foreach (int radius in BoxRadii(sigma))
        {
            Horizontal(plane, scratch, width, height, radius, options);
            Vertical(scratch, plane, width, height, radius, options);
        }
    }

    /// <summary>
    /// The radii of <see cref="Passes"/> box blurs that together approximate a Gaussian of the given standard deviation
    /// (after W. Jarosz / I. Kutskir: the box widths straddle the ideal width so that the variances add up).
    /// </summary>
    internal static int[] BoxRadii(float sigma)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / Passes + 1);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        int upper = lower + 2;
        double mIdeal = (12 * sigma * sigma - Passes * lower * lower - 4 * Passes * lower - 3 * Passes) / (-4.0 * lower - 4);
        int m = (int)Math.Round(mIdeal);
        var radii = new int[Passes];
        for (int i = 0; i < Passes; i++) radii[i] = ((i < m ? lower : upper) - 1) / 2;
        return radii;
    }

    private static void Horizontal(float[] src, float[] dst, int width, int height, int radius, ParallelOptions options)
    {
        float scale = 1f / (2 * radius + 1);
        Parallel.For(0, height, options, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            int row = y * width;
            float sum = 0f;
            for (int k = -radius; k <= radius; k++) sum += src[row + Math.Clamp(k, 0, width - 1)];
            for (int x = 0; x < width; x++)
            {
                dst[row + x] = sum * scale;
                sum += src[row + Math.Min(x + radius + 1, width - 1)] - src[row + Math.Max(x - radius, 0)];
            }
        });
    }

    private static void Vertical(float[] src, float[] dst, int width, int height, int radius, ParallelOptions options)
    {
        float scale = 1f / (2 * radius + 1);
        int strips = (width + ColumnStrip - 1) / ColumnStrip;
        Parallel.For(0, strips, options, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int strip) =>
        {
            int x0 = strip * ColumnStrip, x1 = Math.Min(width, x0 + ColumnStrip), n = x1 - x0;
            Span<float> sums = stackalloc float[ColumnStrip];
            sums.Clear();
            for (int k = -radius; k <= radius; k++)
            {
                int row = Math.Clamp(k, 0, height - 1) * width + x0;
                for (int j = 0; j < n; j++) sums[j] += src[row + j];
            }
            for (int y = 0; y < height; y++)
            {
                int row = y * width + x0;
                int add = Math.Min(y + radius + 1, height - 1) * width + x0;
                int remove = Math.Max(y - radius, 0) * width + x0;
                for (int j = 0; j < n; j++)
                {
                    dst[row + j] = sums[j] * scale;
                    sums[j] += src[add + j] - src[remove + j];
                }
            }
        });
    }
}
