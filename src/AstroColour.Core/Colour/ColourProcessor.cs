using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AstroColour.Core.Curves;
using AstroColour.Core.Stars;

namespace AstroColour.Core.Colour;

public enum PreviewMode
{
    /// <summary>The image with the base adjustments (narrowband, curves, saturation, purple removal, hue curves), colour adjustments and stars applied.</summary>
    Adjusted,
    /// <summary>The unedited image, for before/after comparison.</summary>
    Original,
    /// <summary>The adjusted image where the mask adjustment selects it, greyscale elsewhere (without stars).</summary>
    Mask,
}

/// <summary>The shares of pixels the edits clip to black and to white, as shown by the clipping overlay.</summary>
public readonly record struct ClippingStats(double Shadows, double Highlights);

public static class ColourProcessor
{
    /// <summary>Outputs this close to 0 or 1 count as clipped: they show as pure black or white on screen.</summary>
    public const float ClipThreshold = 0.5f / 255f;

    /// <summary>
    /// Renders the image into an 8-bit BGRA buffer of Width×Height×4 bytes. <paramref name="narrowband"/> normalisation
    /// is applied first, then <paramref name="curves"/>, then <paramref name="saturation"/>, then <paramref name="removePurple"/>,
    /// then <paramref name="hueCurves"/> (see <see cref="BaseAdjustments"/>), then the colour adjustments, then <paramref name="stars"/> (which must match
    /// the image's size).
    /// </summary>
    /// <param name="saturation">Saturation change for the whole image, −100 (grey) to +100 (doubled).</param>
    /// <param name="removePurple">How far green moves towards the red/blue average, 0–1.</param>
    /// <param name="showClipping">
    /// In <see cref="PreviewMode.Adjusted"/> mode, paint pixels the edits push to black blue, those pushed to white red,
    /// and those with channels pushed both ways magenta. A channel already at black or white in the original doesn't
    /// count, and the stars are left out, since their cores are meant to be white.
    /// </param>
    /// <returns>The shares of pixels clipped, if <paramref name="showClipping"/>; otherwise zero.</returns>
    public static ClippingStats RenderPreview(RgbImage image, IReadOnlyList<ColourAdjustment> adjustments, CurveSet? curves,
        PreviewMode mode, ColourAdjustment? mask, byte[] bgra, CancellationToken cancellation = default, StarLayer? stars = null,
        float saturation = 0f, NarrowbandNormalization? narrowband = null, float removePurple = 0f, HueCurveSet? hueCurves = null,
        bool showClipping = false, float pixelScale = 1f, MaskCache? maskCache = null)
    {
        var baseAdjustments = mode == PreviewMode.Original ? null
            : BaseAdjustments.Create(narrowband, curves is null ? null : new CurveLookup(curves), saturation, removePurple,
                hueCurves is null ? null : new HueCurveLookup(hueCurves));
        var set = new AdjustmentSet(adjustments);
        var maskSet = mode == PreviewMode.Mask && mask is not null ? new AdjustmentSet([mask], includeAll: true) : null;
        if (mode == PreviewMode.Original) set = new AdjustmentSet([]);
        if (mode != PreviewMode.Adjusted || stars is { IsEmpty: true }) stars = null;
        if (stars is not null && !stars.Fits(image.Width, image.Height))
            throw new ArgumentException("The stars image is not the same size as the image.", nameof(stars));

        float[] rp = image.R, gp = image.G, bp = image.B;
        int width = image.Width;
        string? baseKey = maskCache is not null && (set.NeedsMasks || maskSet is { NeedsMasks: true })
            ? MaskCache.BaseKey(baseAdjustments?.Narrowband, curves, saturation, removePurple, hueCurves) : null;
        set.BuildMasks(rp, gp, bp, width, image.Height, baseAdjustments, pixelScale, cancellation, maskCache, rp, baseKey);
        maskSet?.BuildMasks(rp, gp, bp, width, image.Height, baseAdjustments, pixelScale, cancellation, maskCache, rp, baseKey);
        var options = new ParallelOptions { CancellationToken = cancellation };
        bool clipping = showClipping && mode == PreviewMode.Adjusted;
        long shadowCount = 0, highlightCount = 0;

        Parallel.For(0, image.Height, options, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            var output = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
            int row = y * width;
            int rowShadows = 0, rowHighlights = 0;
            for (int i = row; i < row + width; i++)
            {
                float r = rp[i], g = gp[i], b = bp[i];
                baseAdjustments?.Apply(ref r, ref g, ref b);

                if (maskSet is not null)
                {
                    float cr = Math.Clamp(r, 0f, 1f), cg = Math.Clamp(g, 0f, 1f), cb = Math.Clamp(b, 0f, 1f);
                    set.Adjust(ref r, ref g, ref b, i);
                    float w = maskSet.MaskWeight(i, cr, cg, cb);
                    float grey = ColourMath.Luminance(cr, cg, cb);
                    r = grey + (r - grey) * w;
                    g = grey + (g - grey) * w;
                    b = grey + (b - grey) * w;
                }
                else
                {
                    set.Adjust(ref r, ref g, ref b, i);
                    if (clipping)
                    {
                        int flags = ClipFlags(rp[i], r) | ClipFlags(gp[i], g) | ClipFlags(bp[i], b);
                        if (flags != 0)
                        {
                            rowShadows += flags & 1;
                            rowHighlights += flags >> 1;
                            output[i] = 0xFF000000u | ((flags & 2) != 0 ? 0xFF0000u : 0u) | ((flags & 1) != 0 ? 0xFFu : 0u);
                            continue;
                        }
                    }
                    stars?.Apply(i, ref r, ref g, ref b);
                }

                output[i] = 0xFF000000u | (uint)ToByte(r) << 16 | (uint)ToByte(g) << 8 | ToByte(b);
            }
            if (rowShadows != 0) Interlocked.Add(ref shadowCount, rowShadows);
            if (rowHighlights != 0) Interlocked.Add(ref highlightCount, rowHighlights);
        });
        return clipping && image.PixelCount > 0
            ? new ClippingStats(shadowCount / (double)image.PixelCount, highlightCount / (double)image.PixelCount)
            : default;
    }

    /// <summary>1 if the edits took a channel from above black to black, 2 if from below white to white, else 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int ClipFlags(float before, float after) =>
        (after <= ClipThreshold && before > ClipThreshold ? 1 : 0) |
        (after >= 1f - ClipThreshold && before < 1f - ClipThreshold ? 2 : 0);

    /// <summary>
    /// Applies the narrowband normalisation, then the curves, then the global saturation, then the purple removal,
    /// then the hue curves, then the adjustments, then the stars at full precision, returning new planes for saving. A single-channel
    /// image takes only the curves and the stars' luminance.
    /// </summary>
    /// <param name="saturation">Saturation change for the whole image, −100 (grey) to +100 (doubled).</param>
    /// <param name="removePurple">How far green moves towards the red/blue average, 0–1.</param>
    public static float[][] Apply(IAstroImage image, IReadOnlyList<ColourAdjustment> adjustments, CurveSet? curves = null,
        StarLayer? stars = null, float saturation = 0f, NarrowbandNormalization? narrowband = null, float removePurple = 0f,
        HueCurveSet? hueCurves = null, MaskCache? maskCache = null)
    {
        var planes = image.Planes.Select(p => (float[])p.Clone()).ToArray();
        var lookup = curves is null ? null : new CurveLookup(curves, monochrome: !image.IsColour);
        if (lookup is { IsEmpty: true }) lookup = null;
        if (stars is { IsEmpty: true }) stars = null;
        if (stars is not null && !stars.Fits(image.Width, image.Height))
            throw new ArgumentException("The stars image is not the same size as the image.", nameof(stars));
        int width = image.Width;

        if (!image.IsColour)
        {
            if (lookup is null && stars is null) return planes;
            float[] p = planes[0];
            Parallel.For(0, image.Height, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
            {
                int row = y * width;
                for (int i = row; i < row + width; i++)
                {
                    float v = lookup?.ApplyMono(p[i]) ?? p[i];
                    p[i] = stars?.ApplyMono(i, v) ?? v;
                }
            });
            return planes;
        }

        var baseAdjustments = BaseAdjustments.Create(narrowband, lookup, saturation, removePurple,
            hueCurves is null ? null : new HueCurveLookup(hueCurves));
        var set = new AdjustmentSet(adjustments);
        if (baseAdjustments is null && set.IsEmpty && stars is null) return planes;

        float[] rp = planes[0], gp = planes[1], bp = planes[2];
        // Keyed on the image's own planes (these are copies), which a full-size preview shares, so its masks are reused.
        string? baseKey = maskCache is not null && set.NeedsMasks
            ? MaskCache.BaseKey(baseAdjustments?.Narrowband, curves, saturation, removePurple, hueCurves) : null;
        set.BuildMasks(rp, gp, bp, width, image.Height, baseAdjustments, cancellation: default, cache: maskCache,
            source: image.Planes[0], baseKey: baseKey);
        Parallel.For(0, image.Height, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            int row = y * width;
            for (int i = row; i < row + width; i++)
            {
                float r = rp[i], g = gp[i], b = bp[i];
                bool changed = baseAdjustments is not null;
                baseAdjustments?.Apply(ref r, ref g, ref b);
                changed |= set.Adjust(ref r, ref g, ref b, i);
                if (stars is not null)
                {
                    stars.Apply(i, ref r, ref g, ref b);
                    changed = true;
                }
                if (changed)
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
