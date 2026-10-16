using System.Runtime.CompilerServices;
using AstroColour.Core.Curves;

namespace AstroColour.Core.Colour;

/// <summary>
/// The whole-image changes made before the colour editor, in order: narrowband normalisation, the curves, global
/// saturation, then purple removal. The colour editor, its picker and its curves' histogram see the image after these.
/// </summary>
/// <param name="Curves">The Global tab's curves, or null for none.</param>
/// <param name="Saturation">Saturation change for the whole image, −100 (grey) to +100 (doubled).</param>
/// <param name="RemovePurple">How far green moves towards the red/blue average, 0–1.</param>
public sealed record BaseAdjustments(NarrowbandNormalization? Narrowband, CurveLookup? Curves, float Saturation, float RemovePurple)
{
    /// <summary>Null when nothing would change.</summary>
    public static BaseAdjustments? Create(NarrowbandNormalization? narrowband, CurveLookup? curves = null,
        float saturation = 0f, float removePurple = 0f)
    {
        if (curves is { IsEmpty: true }) curves = null;
        return narrowband is null && curves is null && saturation == 0f && removePurple == 0f
            ? null
            : new BaseAdjustments(narrowband, curves, saturation, removePurple);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(ref float r, ref float g, ref float b)
    {
        Narrowband?.Apply(ref r, ref g, ref b);
        Curves?.Apply(ref r, ref g, ref b);
        if (Saturation != 0f) Saturate(ref r, ref g, ref b, Saturation);
        if (RemovePurple != 0f) RemovePurpleFrom(r, ref g, b, RemovePurple);
    }

    /// <summary>Scales a pixel's HSL saturation by 1 + <paramref name="amount"/>/100, clamping it to [0,1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Saturate(ref float r, ref float g, ref float b, float amount)
    {
        ColourMath.RgbToHsl(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f),
            out float h, out float s, out float l, out _);
        ColourMath.HslToRgb(h, Math.Clamp(s * (1f + amount / 100f), 0f, 1f), l, out r, out g, out b);
    }

    /// <summary>
    /// Moves green towards the average of red and blue by <paramref name="amount"/> (0–1), as the PixelMath
    /// <c>G: $T[1] + x * (($T[0] + $T[2]) / 2 - $T[1])</c>. It lifts green where red and blue outweigh it,
    /// turning purple towards neutral.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RemovePurpleFrom(float r, ref float g, float b, float amount) =>
        g += amount * ((r + b) * 0.5f - g);
}
