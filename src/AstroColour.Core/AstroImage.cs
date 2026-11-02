using AstroColour.Core.Fits;
using AstroColour.Core.Jpeg;
using AstroColour.Core.Png;
using AstroColour.Core.Raw;
using AstroColour.Core.Tiff;
using AstroColour.Core.Xisf;

namespace AstroColour.Core;

/// <summary>An image file decoded into normalised [0,1] float planes that can be saved back in its own format.</summary>
public interface IAstroImage
{
    int Width { get; }
    int Height { get; }
    int Channels { get; }
    string ColorSpace { get; }

    /// <summary>The sample format stored in the file, for display (e.g. "UInt16", "Float32").</summary>
    string SampleFormatName { get; }

    /// <summary>Bits per stored sample: 8, 16, 32 or 64.</summary>
    int BitsPerSample => 32;

    /// <summary>True when the file stores floating-point samples.</summary>
    bool IsFloat => true;

    /// <summary>The image's FITS header keywords, carried into a file saved in another format that can hold them.</summary>
    IReadOnlyList<FitsKeyword> FitsKeywords => [];

    /// <summary>
    /// True if pixel row 1 of the image's WCS keywords is the bottom row as displayed (see <see cref="Wcs.CropShift"/>):
    /// for bottom-up FITS files, and for XISF files, whose keywords PixInsight writes for a bottom-left origin.
    /// </summary>
    bool WcsFirstRowAtBottom => false;

    /// <summary>Pixel data, one plane per channel, normalised to [0,1] (floats may exceed this range).</summary>
    float[][] Planes { get; }

    bool IsColour => Channels >= 3;
    int PixelCount => Width * Height;

    /// <summary>
    /// Writes the file in its original format, replacing the pixels with <paramref name="planes"/> if given,
    /// then cropping it to <paramref name="crop"/> and turning it by <paramref name="rotation"/>. A cropped image keeps
    /// its astrometric solution, with the reference pixel moved; a rotated image loses it.
    /// </summary>
    void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None, CropRect? crop = null);
}

public enum ImageFormat { Xisf, Fits, Tiff, Png, Jpeg }

/// <summary>A file format an image can be saved in.</summary>
public sealed record ImageFileType(ImageFormat Format, string Name, string[] Extensions);

public static class AstroImageFile
{
    public static readonly string[] XisfExtensions = [".xisf"];
    public static readonly string[] FitsExtensions = [".fits", ".fit", ".fts"];
    public static readonly string[] TiffExtensions = [".tif", ".tiff"];
    public static readonly string[] PngExtensions = [".png"];
    public static readonly string[] JpegExtensions = [".jpg", ".jpeg"];

    /// <summary>Camera raw formats, which can be opened but not saved.</summary>
    public static readonly string[] RawExtensions =
        [".nef", ".nrw", ".cr2", ".cr3", ".arw", ".raf", ".orf", ".rw2", ".pef", ".dng", ".srw"];

    /// <summary>The extensions of files that can be opened.</summary>
    public static readonly string[] AllExtensions = [.. XisfExtensions, .. FitsExtensions, .. TiffExtensions, .. RawExtensions];

    /// <summary>Every format an image can be saved in.</summary>
    public static readonly IReadOnlyList<ImageFileType> FileTypes =
    [
        new(ImageFormat.Xisf, "XISF image", XisfExtensions),
        new(ImageFormat.Fits, "FITS image", FitsExtensions),
        new(ImageFormat.Tiff, "TIFF image", TiffExtensions),
        new(ImageFormat.Png, "PNG image", PngExtensions),
        new(ImageFormat.Jpeg, "JPEG image", JpegExtensions),
    ];

    public static bool IsFits(string path) => HasExtension(path, FitsExtensions);

    public static bool IsTiff(string path) => HasExtension(path, TiffExtensions);

    /// <summary>True for a camera raw file, which can only be saved to a new file in another format.</summary>
    public static bool IsRaw(string path) => HasExtension(path, RawExtensions);

    /// <summary>Loads an XISF, FITS, TIFF or camera raw file, chosen by its extension.</summary>
    public static IAstroImage Load(string path) =>
        IsFits(path) ? FitsFile.Load(path) : IsTiff(path) ? TiffFile.Load(path) : IsRaw(path) ? RawFile.Load(path) : XisfFile.Load(path);

    /// <summary>The format a file is saved in, chosen by its extension, or null if no format uses it.</summary>
    public static ImageFormat? FormatOf(string path) =>
        FileTypes.FirstOrDefault(t => HasExtension(path, t.Extensions))?.Format;

    /// <summary>The format <paramref name="image"/> was loaded from, or for a raw file the one it is saved in by default.</summary>
    public static ImageFormat FormatOf(IAstroImage image) => image switch
    {
        FitsFile => ImageFormat.Fits,
        TiffFile or RawFile => ImageFormat.Tiff,
        _ => ImageFormat.Xisf,
    };

    /// <summary>
    /// The formats <paramref name="image"/> can be saved in, the one <paramref name="path"/> uses first
    /// with that file's own extension first.
    /// </summary>
    public static IReadOnlyList<ImageFileType> SaveFileTypes(IAstroImage image, string path)
    {
        var current = FormatOf(path) ?? FormatOf(image);
        string own = Path.GetExtension(path).ToLowerInvariant();
        return
        [
            .. FileTypes.Where(t => t.Format == current)
                .Select(t => t with { Extensions = t.Extensions.Contains(own) ? [own, .. t.Extensions.Where(e => e != own)] : t.Extensions }),
            .. FileTypes.Where(t => t.Format != current),
        ];
    }

    /// <summary>
    /// Saves <paramref name="image"/> with its pixels replaced by <paramref name="planes"/> (if given), cropped to
    /// <paramref name="crop"/> and turned by <paramref name="rotation"/>, in the format given by the extension of
    /// <paramref name="path"/>. A crop keeps the WCS keywords valid by moving CRPIX1/2; a rotation drops them.
    /// In the image's own format every piece of metadata is kept (see <see cref="IAstroImage.Save"/>). Another
    /// format gets a new file: XISF, FITS and TIFF keep the sample format where they can, and XISF and FITS
    /// keep the FITS keywords; PNG is 16-bit unless the image is 8-bit, and JPEG is 8-bit. Extra channels
    /// such as alpha are dropped.
    /// </summary>
    public static void Save(IAstroImage image, string path, float[][]? planes = null, Rotation rotation = Rotation.None,
        CropRect? crop = null)
    {
        var format = FormatOf(path)
            ?? throw new NotSupportedException($"Cannot save a “{Path.GetExtension(path)}” file. Choose XISF, FITS, TIFF, PNG or JPEG.");
        if (format == FormatOf(image) && image is not RawFile)
        {
            image.Save(path, planes, rotation, crop);
            return;
        }

        planes ??= image.Planes;
        if (planes.Length != image.Channels || planes.Any(p => p.Length != image.PixelCount))
            throw new ArgumentException("Plane data does not match the image geometry.", nameof(planes));
        var geometry = SaveGeometry.Of(image.Width, image.Height, rotation, crop);
        planes = geometry.Apply(planes[..(image.IsColour ? 3 : 1)]);
        var (width, height) = (geometry.Width, geometry.Height);

        var shift = geometry.Crop is { } c ? Wcs.CropShift(c, image.Height, image.WcsFirstRowAtBottom) : (0, 0);
        var keywords = image.FitsKeywords
            .Where(k => !k.IsLayout && !(rotation != Rotation.None && RotationExtensions.IsWcsKeyword(k.Name)))
            .Select(k => geometry.Crop is null ? k : Wcs.Crop(k, shift))
            .ToList();

        // New FITS files are written top row first, while PixInsight counts XISF keyword rows from the bottom, so
        // a solution carried between the two counts its rows from the other end.
        bool? targetFirstRowAtBottom = format switch
        {
            ImageFormat.Xisf => true,
            ImageFormat.Fits => false,
            _ => null,
        };
        if (rotation == Rotation.None && targetFirstRowAtBottom is { } bottom && bottom != image.WcsFirstRowAtBottom)
            keywords = Wcs.FlipRows(keywords, geometry.Height);
        int bits = image.BitsPerSample;
        bool isFloat = image.IsFloat;

        switch (format)
        {
            case ImageFormat.Xisf:
                var xisfFormat = isFloat ? (bits == 64 ? XisfSampleFormat.Float64 : XisfSampleFormat.Float32)
                    : bits switch { 8 => XisfSampleFormat.UInt8, 16 => XisfSampleFormat.UInt16, _ => XisfSampleFormat.UInt32 };
                XisfFile.Create(width, height, planes, xisfFormat, keywords).Save(path);
                break;
            case ImageFormat.Fits:
                int bitpix = isFloat ? (bits == 64 ? -64 : -32) : bits;
                FitsFile.Create(width, height, planes, bitpix, keywords).Save(path);
                break;
            case ImageFormat.Tiff:
                TiffFile.Create(width, height, planes, isFloat ? Math.Max(bits, 32) : bits, isFloat).Save(path);
                break;
            case ImageFormat.Png:
                PngWriter.Save(path, width, height, planes, sixteenBit: isFloat || bits > 8);
                break;
            case ImageFormat.Jpeg:
                JpegWriter.Save(path, width, height, planes);
                break;
        }
    }

    private static bool HasExtension(string path, string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
