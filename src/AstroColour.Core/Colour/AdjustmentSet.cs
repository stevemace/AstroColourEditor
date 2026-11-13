using System.Runtime.CompilerServices;
using AstroColour.Core.Curves;

namespace AstroColour.Core.Colour;

/// <summary>
/// A snapshot of adjustments prepared for fast per-pixel evaluation: each adjustment's hue
/// weight is tabulated, and a combined table lets unselected pixels be rejected cheaply.
/// </summary>
public sealed class AdjustmentSet
{
    private const int Bins = 720; // half-degree bins, interpolated

    private readonly Entry[] _entries;
    private readonly bool[] _anySelected; // bin i: does any adjustment select hues in [i, i+1]?
    private readonly bool _anyCurves;

    // Blurred selection masks, one per entry (null where the entry has no blur), made by BuildMasks.
    private float[]?[]? _masks;

    private readonly record struct Entry(float[] HueWeights, float ChromaEdge, float LightLow, float LightHigh, float LightSoftness,
        float HueShift, float Saturation, float Lightness, CurveLookup? Curves, float Blur, MaskCache.SelectionKey Selection);

    /// <param name="adjustments">Adjustments to include; disabled or no-op ones are skipped unless <paramref name="includeAll"/>.</param>
    /// <param name="includeAll">Include every adjustment, e.g. to evaluate a selection mask.</param>
    public AdjustmentSet(IEnumerable<ColourAdjustment> adjustments, bool includeAll = false)
    {
        _entries = adjustments
            .Where(a => includeAll || (a.Enabled && a.HasEdits))
            .Select(a => new Entry(HueTable(a), a.NeutralProtection / 100f,
                a.LightnessMin / 100f, a.LightnessMax / 100f, a.LightnessSmoothness / 100f,
                a.HueShift, a.Saturation / 100f, a.Lightness / 100f, Lookup(a.Curves), a.MaskBlur,
                MaskCache.SelectionKey.Of(a)))
            .ToArray();
        _anyCurves = _entries.Any(e => e.Curves is not null);

        _anySelected = new bool[Bins];
        foreach (var e in _entries)
            for (int i = 0; i < Bins; i++)
                _anySelected[i] |= e.HueWeights[i] > 0f || e.HueWeights[i + 1] > 0f;
    }

    public bool IsEmpty => _entries.Length == 0;

    /// <summary>Whether any adjustment blurs its selection, and so needs <see cref="BuildMasks"/> before use.</summary>
    public bool NeedsMasks => _entries.Any(e => e.Blur > 0f);

    /// <summary>
    /// Makes the blurred selection masks of the adjustments that blur them, from the image as it is before the
    /// colour adjustments (the planes with <paramref name="baseAdjustments"/> applied). After this, pass each pixel's
    /// index to <see cref="Adjust(ref float, ref float, ref float, int)"/> and <see cref="MaskWeight"/>.
    /// </summary>
    /// <param name="pixelScale">Full-size pixels per pixel of these planes, so that a downsampled preview blurs alike.</param>
    /// <param name="cache">
    /// Where to reuse and keep masks. Needs <paramref name="source"/>, an object standing for these source pixels (before
    /// <paramref name="baseAdjustments"/>), and <paramref name="baseKey"/>, from <see cref="MaskCache.BaseKey"/> for those adjustments.
    /// </param>
    public void BuildMasks(float[] rp, float[] gp, float[] bp, int width, int height, BaseAdjustments? baseAdjustments,
        float pixelScale = 1f, CancellationToken cancellation = default, MaskCache? cache = null, object? source = null,
        string? baseKey = null)
    {
        if (!NeedsMasks) return;
        if (source is null || baseKey is null) cache = null;
        var masks = new float[]?[_entries.Length];
        foreach (int k in Enumerable.Range(0, _entries.Length).Where(k => _entries[k].Blur > 0f))
            masks[k] = cache?.Find(source!, baseKey!, _entries[k].Selection, pixelScale);

        // Make the masks not found, once each even if several adjustments share a selection.
        var blurred = Enumerable.Range(0, _entries.Length)
            .Where(k => _entries[k].Blur > 0f && masks[k] is null)
            .GroupBy(k => _entries[k].Selection)
            .Select(g => g.First())
            .ToArray();
        if (blurred.Length == 0)
        {
            _masks = masks;
            return;
        }
        foreach (int k in blurred) masks[k] = new float[width * height];
        var options = new ParallelOptions { CancellationToken = cancellation };
        var entries = _entries;

        Parallel.For(0, height, options, [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int y) =>
        {
            int row = y * width;
            for (int i = row; i < row + width; i++)
            {
                float r = rp[i], g = gp[i], b = bp[i];
                baseAdjustments?.Apply(ref r, ref g, ref b);
                r = Math.Clamp(r, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                b = Math.Clamp(b, 0f, 1f);
                ColourMath.RgbToHsl(r, g, b, out float h, out _, out _, out float chroma);
                if (chroma <= 1e-7f) continue;
                float lum = ColourMath.Luminance(r, g, b);
                foreach (int k in blurred) masks[k]![i] = Weight(entries[k], h, chroma, lum);
            }
        });

        foreach (int k in blurred)
        {
            MaskBlur.Gaussian(masks[k]!, width, height, _entries[k].Blur / pixelScale, cancellation);
            cache?.Add(source!, baseKey!, _entries[k].Selection, pixelScale, masks[k]!);
        }
        for (int k = 0; k < _entries.Length; k++)
            if (_entries[k].Blur > 0f && masks[k] is null)
                masks[k] = masks[blurred.First(j => _entries[j].Selection == _entries[k].Selection)];
        _masks = masks;
    }

    /// <summary>
    /// Selection weight (0..1) of the first adjustment for pixel <paramref name="index"/>, whose colour is given:
    /// from its blurred mask if it has one, otherwise from the colour.
    /// </summary>
    public float MaskWeight(int index, float r, float g, float b) =>
        _masks?[0] is { } mask ? mask[index] : WeightOfColour(r, g, b);

    private static CurveLookup? Lookup(CurveSet curves)
    {
        var lookup = new CurveLookup(curves);
        return lookup.IsEmpty ? null : lookup;
    }

    private static float[] HueTable(ColourAdjustment a)
    {
        var table = new float[Bins + 1];
        var unprotected = a.Clone();
        unprotected.NeutralProtection = 0;
        unprotected.LightnessMin = 0;
        unprotected.LightnessMax = 100;
        for (int i = 0; i <= Bins; i++)
            table[i] = unprotected.Weight(i * (360f / Bins), 1f, 0.5f);
        return table;
    }

    /// <summary>
    /// Selection weight (0..1) of the first adjustment for a pixel's hue, chroma and luminance.
    /// </summary>
    public float Weight(float hue, float chroma, float luminance) =>
        _entries.Length == 0 ? 0f : Weight(_entries[0], hue, chroma, luminance);

    /// <summary>Selection weight (0..1) of the first adjustment for a colour; channels are clamped to [0,1].</summary>
    public float WeightOfColour(float r, float g, float b)
    {
        if (_entries.Length == 0) return 0f;
        r = Math.Clamp(r, 0f, 1f);
        g = Math.Clamp(g, 0f, 1f);
        b = Math.Clamp(b, 0f, 1f);
        ColourMath.RgbToHsl(r, g, b, out float h, out _, out _, out float chroma);
        return chroma <= 1e-7f ? 0f : Weight(_entries[0], h, chroma, ColourMath.Luminance(r, g, b));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EntryWeight(int k, in Entry e, float[]?[]? masks, int index, bool grey, float hue, float chroma, float luminance) =>
        masks?[k] is { } mask ? mask[index] : grey ? 0f : Weight(e, hue, chroma, luminance);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float Weight(in Entry e, float hue, float chroma, float luminance)
    {
        float pos = hue * (Bins / 360f);
        int i = (int)pos;
        if ((uint)i >= Bins) i = Bins - 1;
        float frac = pos - i;
        float w = e.HueWeights[i] + (e.HueWeights[i + 1] - e.HueWeights[i]) * frac;
        if (w <= 0f) return w;
        w *= ColourAdjustment.LightnessWeight(luminance, e.LightLow, e.LightHigh, e.LightSoftness);
        if (w <= 0f || e.ChromaEdge <= 0f) return w;
        float t = Math.Clamp(chroma / e.ChromaEdge, 0f, 1f);
        return w * t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Applies the adjustments to one pixel: first the HSL changes, then each adjustment's curves,
    /// blended in by its selection weight. Each adjustment's weight is measured against the pixel's
    /// original colour, so overlapping ranges combine rather than chain.
    /// Returns false (leaving the pixel untouched) if no adjustment selects it.
    /// </summary>
    public bool Adjust(ref float r, ref float g, ref float b) => Adjust(ref r, ref g, ref b, -1);

    /// <summary>
    /// As <see cref="Adjust(ref float, ref float, ref float)"/>, for pixel <paramref name="index"/> of the planes given to
    /// <see cref="BuildMasks"/>: adjustments with a blurred mask take their weight from it rather than the pixel's colour.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Adjust(ref float r, ref float g, ref float b, int index)
    {
        if (_entries.Length == 0) return false;
        var masks = index >= 0 ? _masks : null;

        float cr = Math.Clamp(r, 0f, 1f), cg = Math.Clamp(g, 0f, 1f), cb = Math.Clamp(b, 0f, 1f);
        float max = MathF.Max(cr, MathF.Max(cg, cb));
        float min = MathF.Min(cr, MathF.Min(cg, cb));
        float chroma = max - min;
        bool grey = chroma <= 1e-7f;
        if (grey && masks is null) return false; // a blurred mask can still reach a grey pixel

        float h = 0f;
        if (!grey)
        {
            if (max == cr) h = (cg - cb) / chroma + (cg < cb ? 6f : 0f);
            else if (max == cg) h = (cb - cr) / chroma + 2f;
            else h = (cr - cg) / chroma + 4f;
            h *= 60f;

            int bin = (int)(h * (Bins / 360f));
            if ((uint)bin >= Bins) bin = Bins - 1;
            if (masks is null && !_anySelected[bin]) return false;
        }

        float l = (max + min) * 0.5f;
        float y = ColourMath.Luminance(cr, cg, cb);
        float dh = 0f, ds = 0f, dl = 0f;
        bool selected = false;
        for (int k = 0; k < _entries.Length; k++)
        {
            ref readonly var e = ref _entries[k];
            float w = EntryWeight(k, e, masks, index, grey, h, chroma, y);
            if (w <= 0f) continue;
            selected = true;
            dh += w * e.HueShift;
            ds += w * e.Saturation;
            dl += w * e.Lightness;
        }
        if (!selected) return false;

        if (dh != 0f || ds != 0f || dl != 0f)
        {
            float s = grey ? 0f : l > 0.5f ? chroma / (2f - max - min) : chroma / (max + min);
            ds = Math.Clamp(ds, -1f, 1f);
            dl = Math.Clamp(dl, -1f, 1f);

            float newS = Math.Clamp(s * (1f + ds), 0f, 1f);
            float newL = dl >= 0f ? l + (1f - l) * dl * 0.5f : l * (1f + dl * 0.5f);
            ColourMath.HslToRgb(h + dh, newS, Math.Clamp(newL, 0f, 1f), out r, out g, out b);
        }

        if (_anyCurves)
        {
            float dr = 0f, dg = 0f, db = 0f;
            for (int k = 0; k < _entries.Length; k++)
            {
                ref readonly var e = ref _entries[k];
                if (e.Curves is null) continue;
                float w = EntryWeight(k, e, masks, index, grey, h, chroma, y);
                if (w <= 0f) continue;
                float tr = r, tg = g, tb = b;
                e.Curves.Apply(ref tr, ref tg, ref tb);
                dr += w * (tr - r);
                dg += w * (tg - g);
                db += w * (tb - b);
            }
            r += dr;
            g += dg;
            b += db;
        }
        return true;
    }
}
