using System.Text.RegularExpressions;

namespace AstroColour.Core;

/// <summary>A rotation of the image in quarter turns, clockwise as displayed (top row first).</summary>
public enum Rotation
{
    None = 0,
    Clockwise = 1,
    HalfTurn = 2,
    Anticlockwise = 3,
}

public static partial class RotationExtensions
{
    /// <summary>The rotation after turning a further <paramref name="quarterTurns"/> clockwise (negative for anticlockwise).</summary>
    public static Rotation Turn(this Rotation rotation, int quarterTurns) => (Rotation)((((int)rotation + quarterTurns) % 4 + 4) % 4);

    /// <summary>True for quarter turns, which swap the width and height.</summary>
    public static bool SwapsAxes(this Rotation rotation) => rotation is Rotation.Clockwise or Rotation.Anticlockwise;

    public static int Degrees(this Rotation rotation) => (int)rotation * 90;

    /// <summary>The size of a <paramref name="width"/>×<paramref name="height"/> image after rotating.</summary>
    public static (int Width, int Height) Size(this Rotation rotation, int width, int height) =>
        rotation.SwapsAxes() ? (height, width) : (width, height);

    /// <summary>Rotates row-major planes of a <paramref name="width"/>×<paramref name="height"/> image, returning new planes.</summary>
    public static float[][] Apply(this Rotation rotation, float[][] planes, int width, int height)
    {
        if (rotation == Rotation.None) return planes;
        return planes.Select(plane =>
        {
            var result = new float[plane.Length];
            Parallel.For(0, height, y =>
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int target = rotation switch
                    {
                        Rotation.Clockwise => x * height + (height - 1 - y),
                        Rotation.HalfTurn => (height - 1 - y) * width + (width - 1 - x),
                        _ => (width - 1 - x) * height + y,
                    };
                    result[target] = plane[row + x];
                }
            });
            return result;
        }).ToArray();
    }

    /// <summary>
    /// True for a FITS keyword that belongs to an astrometric (WCS) solution, which no longer matches
    /// the pixels once the image is rotated.
    /// </summary>
    public static bool IsWcsKeyword(string keyword) => WcsKeyword().IsMatch(keyword.Trim());

    [GeneratedRegex(@"^(WCSAXES|LONPOLE|LATPOLE|PLTSOLVD|(CTYPE|CRPIX|CRVAL|CDELT|CROTA|CUNIT|CRDER|CSYER)\d+|(CD|PC|PV|PS)\d+_\d+|(A|B|AP|BP)_(ORDER|DMAX|\d+_\d+))$",
        RegexOptions.IgnoreCase)]
    private static partial Regex WcsKeyword();
}
