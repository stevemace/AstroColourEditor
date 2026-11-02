using System.Globalization;
using System.Text.RegularExpressions;
using AstroColour.Core.Fits;

namespace AstroColour.Core;

/// <summary>
/// A rectangle of whole pixels to keep when saving, in the image's own pixel coordinates: x to the right from the
/// left column, y down from the top row as displayed, before any <see cref="Rotation"/>.
/// </summary>
public readonly record struct CropRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>True if the rectangle is the whole of a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    public bool IsWhole(int width, int height) => X == 0 && Y == 0 && Width == width && Height == height;

    /// <summary>True if the rectangle is non-empty and lies within a <paramref name="width"/>×<paramref name="height"/> image.</summary>
    public bool FitsIn(int width, int height) => X >= 0 && Y >= 0 && Width > 0 && Height > 0 && Right <= width && Bottom <= height;

    /// <summary>
    /// The crop to apply to a <paramref name="width"/>×<paramref name="height"/> image: null for no crop or one that
    /// keeps the whole image.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The crop does not lie within the image.</exception>
    public static CropRect? Effective(CropRect? crop, int width, int height)
    {
        if (crop is not { } c) return null;
        if (!c.FitsIn(width, height))
            throw new ArgumentOutOfRangeException(nameof(crop), crop, $"The crop does not fit in the {width} × {height} image.");
        return c.IsWhole(width, height) ? null : c;
    }

    /// <summary>Copies the rectangle out of row-major planes of an image <paramref name="imageWidth"/> pixels wide.</summary>
    public float[][] Apply(float[][] planes, int imageWidth)
    {
        var (x0, y0, width, height) = this;
        return planes.Select(plane =>
        {
            var result = new float[width * height];
            for (int y = 0; y < height; y++)
                Array.Copy(plane, (y0 + y) * imageWidth + x0, result, y * width, width);
            return result;
        }).ToArray();
    }

    /// <summary>
    /// The same pixels after the <paramref name="imageWidth"/>×<paramref name="imageHeight"/> image is turned by
    /// <paramref name="rotation"/>, i.e. as the rectangle appears on screen.
    /// </summary>
    public CropRect Rotate(Rotation rotation, int imageWidth, int imageHeight) => rotation switch
    {
        Rotation.Clockwise => new(imageHeight - Bottom, X, Height, Width),
        Rotation.HalfTurn => new(imageWidth - Right, imageHeight - Bottom, Width, Height),
        Rotation.Anticlockwise => new(Y, imageWidth - Right, Height, Width),
        _ => this,
    };

    /// <summary>
    /// The inverse of <see cref="Rotate"/>: a rectangle on the turned image back in the image's own coordinates.
    /// <paramref name="imageWidth"/> and <paramref name="imageHeight"/> are the size before turning.
    /// </summary>
    public CropRect Unrotate(Rotation rotation, int imageWidth, int imageHeight)
    {
        var (turnedWidth, turnedHeight) = rotation.Size(imageWidth, imageHeight);
        var opposite = (Rotation)((4 - (int)rotation) % 4);
        return Rotate(opposite, turnedWidth, turnedHeight);
    }
}

/// <summary>Adjusting an astrometric (WCS) solution in FITS keywords to a cropped image.</summary>
public static partial class Wcs
{
    /// <summary>
    /// How far the FITS pixel grid moves when the image is cropped to <paramref name="crop"/>: the amounts to subtract
    /// from CRPIX1 and CRPIX2. FITS counts pixels from the first row stored; <paramref name="firstRowAtBottom"/> says
    /// whether that row is the bottom of the image as displayed (bottom-up FITS files, and PixInsight's keywords in
    /// XISF files) rather than the top.
    /// </summary>
    public static (int X, int Y) CropShift(CropRect crop, int imageHeight, bool firstRowAtBottom) =>
        (crop.X, firstRowAtBottom ? imageHeight - crop.Bottom : crop.Y);

    /// <summary>The axis (1 or 2) of a reference pixel keyword, CRPIX1 or CRPIX2 (or an alternate such as CRPIX1A), else 0.</summary>
    public static int CrpixAxis(string keyword) =>
        CrpixKeyword().Match(keyword.Trim()) is { Success: true } m ? m.Groups[1].Value[0] - '0' : 0;

    /// <summary>
    /// The keyword with its value moved by the crop if it is a CRPIX keyword; any other keyword is returned as it is.
    /// Distortion terms (SIP) are relative to the reference pixel, so they stay valid. A value that is not a number is
    /// left alone.
    /// </summary>
    public static FitsKeyword Crop(FitsKeyword keyword, (int X, int Y) shift)
    {
        int axis = CrpixAxis(keyword.Name);
        if (axis == 0 || ShiftValue(keyword.Value, axis == 1 ? shift.X : shift.Y) is not { } value) return keyword;
        return keyword with { Value = value };
    }

    /// <summary>A FITS number moved down by <paramref name="shift"/>, or null if <paramref name="value"/> is not a number.</summary>
    public static string? ShiftValue(string value, int shift)
    {
        if (!double.TryParse(value.Trim().Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return null;
        return (v - shift).ToString("0.0##########", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The keywords with their WCS turned upside down in pixel terms, for an image <paramref name="height"/> rows high
    /// whose rows are now counted from the other end: what happens when a top-down FITS file's keywords go into an
    /// XISF file, whose keywords PixInsight counts from the bottom, or the other way round. The sky is unchanged; only
    /// the pixel numbering flips. So CRPIX2 becomes <c>height + 1 − CRPIX2</c>, the second column of the CD or PC matrix
    /// (or CDELT2 when there is neither) changes sign, and SIP distortion terms change sign where the flipped y offset
    /// appears an odd number of times. Other distortion (PV keywords, as in TPV) can't be flipped this way, so the
    /// solution is dropped instead of being left wrong.
    /// </summary>
    public static List<FitsKeyword> FlipRows(IReadOnlyList<FitsKeyword> keywords, int height)
    {
        if (keywords.Any(k => PvKeyword().IsMatch(k.Name.Trim())))
            return keywords.Where(k => !RotationExtensions.IsWcsKeyword(k.Name)).ToList();

        // Alternate solutions (CRPIX1A …) each have their own matrix; note which suffixes use CD or PC.
        var hasMatrix = keywords
            .Select(k => MatrixKeyword().Match(k.Name.Trim()))
            .Where(m => m.Success)
            .Select(m => m.Groups[4].Value.ToUpperInvariant())
            .ToHashSet();

        return keywords.Select(k =>
        {
            string name = k.Name.Trim();
            if (CrpixKeyword().Match(name) is { Success: true } crpix && crpix.Groups[1].Value == "2")
                return Map(k, v => height + 1 - v);
            if (MatrixKeyword().Match(name) is { Success: true } matrix && matrix.Groups[3].Value == "2")
                return Negate(k);
            if (CdeltKeyword().Match(name) is { Success: true } cdelt && !hasMatrix.Contains(cdelt.Groups[1].Value.ToUpperInvariant()))
                return Negate(k);
            if (SipKeyword().Match(name) is { Success: true } sip)
            {
                // Terms are coefficients of u^p v^q, with v the y offset from CRPIX2; B and BP give a y correction.
                bool oddV = int.Parse(sip.Groups[3].Value, CultureInfo.InvariantCulture) % 2 == 1;
                bool yCorrection = sip.Groups[1].Value.StartsWith('B') || sip.Groups[1].Value.StartsWith('b');
                return oddV != yCorrection ? Negate(k) : k;
            }
            return k;
        }).ToList();
    }

    private static FitsKeyword Negate(FitsKeyword k) => Map(k, v => -v);

    private static FitsKeyword Map(FitsKeyword k, Func<double, double> f)
    {
        if (!double.TryParse(k.Value.Trim().Replace('D', 'E').Replace('d', 'e'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return k;
        double result = f(v);
        if (result == 0) result = 0; // no "-0"
        return k with { Value = result.ToString("R", CultureInfo.InvariantCulture) };
    }

    [GeneratedRegex(@"^CRPIX([12])[A-Z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex CrpixKeyword();

    [GeneratedRegex(@"^(CD|PC)([12])_([12])([A-Z]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex MatrixKeyword();

    [GeneratedRegex(@"^CDELT2([A-Z]?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CdeltKeyword();

    [GeneratedRegex(@"^(A|B|AP|BP)_(\d+)_(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SipKeyword();

    [GeneratedRegex(@"^PV\d+_\d+[A-Z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex PvKeyword();
}

/// <summary>The crop and then rotation applied to an image's pixels when it is saved, and the size that results.</summary>
public sealed record SaveGeometry(int SourceWidth, int SourceHeight, CropRect? Crop, Rotation Rotation)
{
    /// <summary>The geometry for a <paramref name="width"/>×<paramref name="height"/> image; a crop of the whole image is none.</summary>
    public static SaveGeometry Of(int width, int height, Rotation rotation, CropRect? crop) =>
        new(width, height, CropRect.Effective(crop, width, height), rotation);

    /// <summary>The size before rotating: the crop's, or the whole image's.</summary>
    public (int Width, int Height) Cropped => Crop is { } c ? (c.Width, c.Height) : (SourceWidth, SourceHeight);

    public int Width => Rotation.Size(Cropped.Width, Cropped.Height).Width;
    public int Height => Rotation.Size(Cropped.Width, Cropped.Height).Height;

    /// <summary>True if the saved image has a different size from the source.</summary>
    public bool ChangesSize => Width != SourceWidth || Height != SourceHeight;

    /// <summary>Crops then turns row-major planes of the source image.</summary>
    public float[][] Apply(float[][] planes)
    {
        if (Crop is { } c) planes = c.Apply(planes, SourceWidth);
        return Rotation.Apply(planes, Cropped.Width, Cropped.Height);
    }
}
