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

    private readonly record struct Entry(float[] HueWeights, float ChromaEdge, float LightLow, float LightHigh, float LightSoftness,
        float HueShift, float Saturation, float Lightness, CurveLookup? Curves);

    /// <param name="adjustments">Adjustments to include; disabled or no-op ones are skipped unless <paramref name="includeAll"/>.</param>
    /// <param name="includeAll">Include every adjustment, e.g. to evaluate a selection mask.</param>
    public AdjustmentSet(IEnumerable<ColourAdjustment> adjustments, bool includeAll = false)
    {
        _entries = adjustments
            .Where(a => includeAll || (a.Enabled && a.HasEdits))
            .Select(a => new Entry(HueTable(a), a.NeutralProtection / 100f,
                a.LightnessMin / 100f, a.LightnessMax / 100f, a.LightnessSmoothness / 100f,
                a.HueShift, a.Saturation / 100f, a.Lightness / 100f, Lookup(a.Curves)))
            .ToArray();
        _anyCurves = _entries.Any(e => e.Curves is not null);

        _anySelected = new bool[Bins];
        foreach (var e in _entries)
            for (int i = 0; i < Bins; i++)
                _anySelected[i] |= e.HueWeights[i] > 0f || e.HueWeights[i + 1] > 0f;
    }

    public bool IsEmpty => _entries.Length == 0;

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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Adjust(ref float r, ref float g, ref float b)
    {
        if (_entries.Length == 0) return false;

        float cr = Math.Clamp(r, 0f, 1f), cg = Math.Clamp(g, 0f, 1f), cb = Math.Clamp(b, 0f, 1f);
        float max = MathF.Max(cr, MathF.Max(cg, cb));
        float min = MathF.Min(cr, MathF.Min(cg, cb));
        float chroma = max - min;
        if (chroma <= 1e-7f) return false;

        float h;
        if (max == cr) h = (cg - cb) / chroma + (cg < cb ? 6f : 0f);
        else if (max == cg) h = (cb - cr) / chroma + 2f;
        else h = (cr - cg) / chroma + 4f;
        h *= 60f;

        int bin = (int)(h * (Bins / 360f));
        if ((uint)bin >= Bins) bin = Bins - 1;
        if (!_anySelected[bin]) return false;

        float l = (max + min) * 0.5f;
        float y = ColourMath.Luminance(cr, cg, cb);
        float dh = 0f, ds = 0f, dl = 0f;
        bool selected = false;
        foreach (ref readonly var e in _entries.AsSpan())
        {
            float w = Weight(e, h, chroma, y);
            if (w <= 0f) continue;
            selected = true;
            dh += w * e.HueShift;
            ds += w * e.Saturation;
            dl += w * e.Lightness;
        }
        if (!selected) return false;

        if (dh != 0f || ds != 0f || dl != 0f)
        {
            float s = l > 0.5f ? chroma / (2f - max - min) : chroma / (max + min);
            ds = Math.Clamp(ds, -1f, 1f);
            dl = Math.Clamp(dl, -1f, 1f);

            float newS = Math.Clamp(s * (1f + ds), 0f, 1f);
            float newL = dl >= 0f ? l + (1f - l) * dl * 0.5f : l * (1f + dl * 0.5f);
            ColourMath.HslToRgb(h + dh, newS, Math.Clamp(newL, 0f, 1f), out r, out g, out b);
        }

        if (_anyCurves)
        {
            float dr = 0f, dg = 0f, db = 0f;
            foreach (ref readonly var e in _entries.AsSpan())
            {
                if (e.Curves is null) continue;
                float w = Weight(e, h, chroma, y);
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
