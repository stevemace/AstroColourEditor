using System.Runtime.CompilerServices;

namespace AstroColour.Core.Colour;

/// <summary>
/// One colour range and the HSL changes applied to it, in the style of Capture One's
/// Advanced colour editor. Pixels whose hue lies within <see cref="Range"/> degrees of
/// <see cref="TargetHue"/> receive the full adjustment, which then fades out over a
/// further <see cref="Smoothness"/> degrees. The selection can also be limited to pixels
/// whose HSL lightness lies between <see cref="LightnessMin"/> and <see cref="LightnessMax"/>.
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

    /// <summary>HSL lightness, as a percentage, below which pixels are excluded.</summary>
    public float LightnessMin { get; set; }

    /// <summary>HSL lightness, as a percentage, above which pixels are excluded.</summary>
    public float LightnessMax { get; set; } = 100f;

    /// <summary>Width, as a percentage of lightness, of the falloff beyond the lightness limits.</summary>
    public float LightnessSmoothness { get; set; } = 10f;

    /// <summary>Hue rotation in degrees.</summary>
    public float HueShift { get; set; }

    /// <summary>Saturation change, −100 (grey) to +100 (doubled).</summary>
    public float Saturation { get; set; }

    /// <summary>Lightness change, −100 to +100.</summary>
    public float Lightness { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>The averaged colour that was picked from the image.</summary>
    public float SampleR { get; set; }
    public float SampleG { get; set; }
    public float SampleB { get; set; }

    public bool HasEdits => HueShift != 0 || Saturation != 0 || Lightness != 0;

    public ColourAdjustment Clone() => (ColourAdjustment)MemberwiseClone();

    public void ResetEdits()
    {
        HueShift = 0;
        Saturation = 0;
        Lightness = 0;
    }

    /// <summary>How strongly (0..1) a pixel with the given hue, chroma and lightness is selected.</summary>
    public float Weight(float hue, float chroma, float lightness)
    {
        float chromaWeight = NeutralProtection <= 0 ? 1f : SmoothStep(0f, NeutralProtection / 100f, chroma);
        if (chromaWeight <= 0f) return 0f;

        float lightnessWeight = LightnessWeight(lightness, LightnessMin / 100f, LightnessMax / 100f, LightnessSmoothness / 100f);
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
