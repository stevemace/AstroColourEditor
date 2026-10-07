using AstroColour.Core.Fits;
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

    /// <summary>Pixel data, one plane per channel, normalised to [0,1] (floats may exceed this range).</summary>
    float[][] Planes { get; }

    bool IsColour => Channels >= 3;
    int PixelCount => Width * Height;

    /// <summary>Writes the file in its original format, replacing the pixels with <paramref name="planes"/> if given.</summary>
    void Save(string path, float[][]? planes = null);
}

public static class AstroImageFile
{
    public static readonly string[] XisfExtensions = [".xisf"];
    public static readonly string[] FitsExtensions = [".fits", ".fit", ".fts"];
    public static readonly string[] TiffExtensions = [".tif", ".tiff"];
    public static readonly string[] AllExtensions = [.. XisfExtensions, .. FitsExtensions, .. TiffExtensions];

    public static bool IsFits(string path) => HasExtension(path, FitsExtensions);

    public static bool IsTiff(string path) => HasExtension(path, TiffExtensions);

    /// <summary>Loads an XISF, FITS or TIFF file, chosen by its extension.</summary>
    public static IAstroImage Load(string path) =>
        IsFits(path) ? FitsFile.Load(path) : IsTiff(path) ? TiffFile.Load(path) : XisfFile.Load(path);

    /// <summary>The file type name and extensions that a file loaded from <paramref name="path"/> is saved as, its own extension first.</summary>
    public static (string Name, string[] Extensions) SaveFormat(string path)
    {
        var (name, extensions) = IsFits(path) ? ("FITS image", FitsExtensions)
            : IsTiff(path) ? ("TIFF image", TiffExtensions)
            : ("XISF image", XisfExtensions);
        string own = Path.GetExtension(path).ToLowerInvariant();
        return (name, extensions.Contains(own) ? [own, .. extensions.Where(e => e != own)] : extensions);
    }

    private static bool HasExtension(string path, string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
