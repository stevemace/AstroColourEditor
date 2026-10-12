using System.Runtime.CompilerServices;
using AstroColour.Core.Colour;

namespace AstroColour.Core.Stars;

/// <summary>
/// A stars-only image added back over the edited starless image with the pixel math
/// <c>~((~starless) * (~(stars * x)))</c>, a screen blend where <c>x</c> (<see cref="Brightness"/>, 0–1)
/// sets how bright the stars are.
/// </summary>
public sealed class StarLayer
{
    private readonly float[] _r, _g, _b;

    public StarLayer(RgbImage stars, float brightness)
    {
        Stars = stars;
        Brightness = Math.Clamp(brightness, 0f, 1f);
        _r = stars.R;
        _g = stars.G;
        _b = stars.B;
    }

    public RgbImage Stars { get; }

    public float Brightness { get; }

    /// <summary>True when the stars would leave the image unchanged.</summary>
    public bool IsEmpty => Brightness <= 0f;

    /// <summary>True if the stars line up pixel for pixel with an image of this size.</summary>
    public bool Fits(int width, int height) => Stars.Width == width && Stars.Height == height;

    /// <summary><c>~((~starless) * (~(stars * x)))</c>, with the star value clipped to [0,1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Screen(float starless, float stars, float brightness) =>
        1f - (1f - starless) * (1f - Math.Clamp(stars, 0f, 1f) * brightness);

    /// <summary>Adds the stars at pixel <paramref name="i"/> to a colour pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(int i, ref float r, ref float g, ref float b)
    {
        r = Screen(r, _r[i], Brightness);
        g = Screen(g, _g[i], Brightness);
        b = Screen(b, _b[i], Brightness);
    }

    /// <summary>Adds the stars' luminance at pixel <paramref name="i"/> to a single-channel pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ApplyMono(int i, float v) =>
        Screen(v, 0.2126f * _r[i] + 0.7152f * _g[i] + 0.0722f * _b[i], Brightness);
}
