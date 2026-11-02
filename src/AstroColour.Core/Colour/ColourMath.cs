using System.Runtime.CompilerServices;

namespace AstroColour.Core.Colour;

public static class ColourMath
{
    /// <summary>RGB in [0,1] to hue (degrees), HSL saturation, HSL lightness and chroma.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l, out float chroma)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        l = (max + min) * 0.5f;
        chroma = max - min;
        if (chroma <= 1e-7f)
        {
            h = 0f;
            s = 0f;
            return;
        }

        s = l > 0.5f ? chroma / (2f - max - min) : chroma / (max + min);
        if (max == r) h = (g - b) / chroma + (g < b ? 6f : 0f);
        else if (max == g) h = (b - r) / chroma + 2f;
        else h = (r - g) / chroma + 4f;
        h *= 60f;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s <= 0f)
        {
            r = g = b = l;
            return;
        }

        h = ((h % 360f) + 360f) % 360f / 360f;
        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        r = HueToChannel(p, q, h + 1f / 3f);
        g = HueToChannel(p, q, h);
        b = HueToChannel(p, q, h - 1f / 3f);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float HueToChannel(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }

    /// <summary>Rec. 709 luminance of a colour.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Luminance(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;

    public static float Hue(float r, float g, float b)
    {
        RgbToHsl(r, g, b, out float h, out _, out _, out _);
        return h;
    }

    public static string HueName(float hue)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        return hue switch
        {
            < 15f => "Red",
            < 45f => "Orange",
            < 70f => "Yellow",
            < 150f => "Green",
            < 195f => "Cyan",
            < 255f => "Blue",
            < 285f => "Purple",
            < 335f => "Magenta",
            _ => "Red",
        };
    }
}
