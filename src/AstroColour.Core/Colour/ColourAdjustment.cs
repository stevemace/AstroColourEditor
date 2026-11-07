using System.Runtime.CompilerServices;
using AstroColour.Core.Curves;

namespace AstroColour.Core.Colour;

/// <summary>
/// One colour range and the changes applied to it, in the style of Capture One's
/// Advanced colour editor: HSL shifts, tone curves, or both. Pixels whose hue lies within <see cref="Range"/> degrees of
/// <see cref="TargetHue"/> receive the full adjustment, which then fades out over a
/// further <see cref="Smoothness"/> degrees. The selection can also be limited to pixels
/// whose brightness (Rec. 709 luminance, 0.2126 R + 0.7152 G + 0.0722 B: how bright the colour looks) lies between
/// <see cref="LightnessMin"/> and <see cref="LightnessMax"/>.
/// </summary>
public sealed class ColourAdjustment
{
    /// <summary>Centre of the selected hue range, in degrees [0,360).</summary>
    public float TargetHue { get; set; }

    /// <summary>Half-width, in degrees, of the range that receives the full adjustment.</summary>
    public float Range { get; set; } = 20f;

    /// <summary>Width, in degrees, of the falloff beyond <see cref="Range"/>.</summary>
    public float Smoothness { get; set; } = 25f;

    /// <summary>
    /// Chroma (max−min of RGB, as a percentage) below which pixels are progressively excluded,
    /// so that the neutral background and white stars are left alone.
    /// </summary>
    public float NeutralProtection { get; set; } = 5f;

    /// <summary>Luminance, as a percentage, below which pixels are excluded.</summary>
    public float LightnessMin { get; set; }

    /// <summary>Luminance, as a percentage, above which pixels are excluded.</summary>
    public float LightnessMax { get; set; } = 100f;

    /// <summary>Width, as a percentage of luminance, of the falloff beyond the luminance limits.</summary>
    public float LightnessSmoothness { get; set; } = 10f;

    /// <summary>Hue rotation in degrees.</summary>
    public float HueShift { get; set; }

    /// <summary>Saturation change, −100 (grey) to +100 (doubled).</summary>
    public float Saturation { get; set; }

    /// <summary>Lightness change, −100 to +100.</summary>
    public float Lightness { get; set; }

    /// <summary>Tone curves applied to the selected colours, after the HSL changes.</summary>
    public CurveSet Curves { get; private set; } = new();

    public bool Enabled { get; set; } = true;

    /// <summary>The averaged colour that was picked from the image.</summary>
    public float SampleR { get; set; }
    public float SampleG { get; set; }
    public float SampleB { get; set; }

    public bool HasHslEdits => HueShift != 0 || Saturation != 0 || Lightness != 0;

    public bool HasEdits => HasHslEdits || !Curves.IsIdentity;

    public ColourAdjustment Clone()
    {
        var copy = (ColourAdjustment)MemberwiseClone();
        copy.Curves = Curves.Clone();
        return copy;
    }

    public void ResetHsl()
    {
        HueShift = 0;
        Saturation = 0;
        Lightness = 0;
    }

    /// <summary>The widest <see cref="Range"/>, and so a selection of half the hue circle.</summary>
    public const float MaxRange = 90f;

    /// <summary>The narrowest <see cref="Range"/>.</summary>
    public const float MinRange = 1f;

    /// <summary>
    /// Widens the selection just enough to select a colour fully (Shift-click): the hue range grows on the colour's
    /// side, keeping its other edge where it is, and the luminance limits and neutral protection make room for it.
    /// Returns false if the colour was already fully selected.
    /// </summary>
    /// <param name="chroma">The colour's chroma, max − min of RGB, in [0,1].</param>
    /// <param name="luminance">The colour's luminance in [0,1].</param>
    public bool Include(float hue, float chroma, float luminance)
    {
        bool changed = false;
        float s = SignedHueDelta(hue, TargetHue);
        if (MathF.Abs(s) > Range)
        {
            // The edges relative to the current centre, with the far one fixed (unless that makes the range too wide).
            float low = s > 0 ? -Range : s, high = s > 0 ? s : Range;
            if (high - low > 2 * MaxRange)
            {
                if (s > 0) low = high - 2 * MaxRange;
                else high = low + 2 * MaxRange;
            }
            TargetHue = WrapHue(TargetHue + (low + high) / 2);
            Range = (high - low) / 2;
            changed = true;
        }

        float lum = luminance * 100f;
        if (lum < LightnessMin) { LightnessMin = MathF.Floor(lum); changed = true; }
        if (lum > LightnessMax) { LightnessMax = MathF.Min(100f, MathF.Ceiling(lum)); changed = true; }

        float chromaPercent = MathF.Floor(chroma * 100f);
        if (chromaPercent < NeutralProtection) { NeutralProtection = MathF.Max(0f, chromaPercent); changed = true; }
        return changed;
    }

    /// <summary>
    /// Narrows the hue range so that a hue is left out entirely (Alt-click): the range shrinks from the hue's side,
    /// keeping its other edge where it is, and the falloff shortens if there is not room for all of it.
    /// Returns false if the hue was already left out, or lies so near the far edge that nothing would be left.
    /// </summary>
    public bool Exclude(float hue)
    {
        const float margin = 1f; // degrees between the falloff's end and the hue being left out
        float s = SignedHueDelta(hue, TargetHue);
        float side = s >= 0 ? 1f : -1f;
        float distance = MathF.Abs(s);
        if (distance >= Range + Smoothness) return false;

        // Work on the hue's side as if it were positive: the far edge is at -Range, the hue at +distance.
        float available = distance - margin + Range; // from the far edge to the end of the new falloff
        if (available < 4 * MinRange) return false;
        float smoothness = MathF.Min(Smoothness, available / 2);
        float near = -Range + (available - smoothness);
        float centre = (near - Range) / 2;
        TargetHue = WrapHue(TargetHue + side * centre);
        Range = (near + Range) / 2;
        Smoothness = smoothness;
        return true;
    }

    /// <summary>The hue difference <paramref name="a"/> − <paramref name="b"/>, in (−180, 180] degrees.</summary>
    public static float SignedHueDelta(float a, float b)
    {
        float d = (a - b) % 360f;
        if (d > 180f) d -= 360f;
        else if (d <= -180f) d += 360f;
        return d;
    }

    /// <summary>A hue in degrees brought into [0,360).</summary>
    public static float WrapHue(float h)
    {
        h %= 360f;
        return h < 0f ? h + 360f : h;
    }

    /// <summary>How strongly (0..1) a pixel with the given hue, chroma and luminance is selected.</summary>
    public float Weight(float hue, float chroma, float luminance)
    {
        float chromaWeight = NeutralProtection <= 0 ? 1f : SmoothStep(0f, NeutralProtection / 100f, chroma);
        if (chromaWeight <= 0f) return 0f;

        float lightnessWeight = LightnessWeight(luminance, LightnessMin / 100f, LightnessMax / 100f, LightnessSmoothness / 100f);
        if (lightnessWeight <= 0f) return 0f;

        float distance = HueDistance(hue, TargetHue);
        float hueWeight;
        if (distance <= Range) hueWeight = 1f;
        else if (Smoothness <= 0f || distance >= Range + Smoothness) return 0f;
        else hueWeight = 1f - SmoothStep(Range, Range + Smoothness, distance);

        return hueWeight * chromaWeight * lightnessWeight;
    }

    /// <summary>
    /// 1 for lightness within [<paramref name="low"/>, <paramref name="high"/>], fading to 0 over
    /// <paramref name="softness"/> beyond either limit. All values are fractions in [0,1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float LightnessWeight(float lightness, float low, float high, float softness)
    {
        float w = 1f;
        if (lightness < low) w = softness <= 0f ? 0f : SmoothStep(low - softness, low, lightness);
        if (lightness > high) w *= softness <= 0f ? 0f : 1f - SmoothStep(high, high + softness, lightness);
        return w;
    }

    public static float HueDistance(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
