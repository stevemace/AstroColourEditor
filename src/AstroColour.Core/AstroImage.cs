using AstroColour.Core.Fits;
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
    public static readonly string[] AllExtensions = [.. XisfExtensions, .. FitsExtensions];

    public static bool IsFits(string path) =>
        FitsExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads an XISF or FITS file, chosen by its extension.</summary>
    public static IAstroImage Load(string path) => IsFits(path) ? FitsFile.Load(path) : XisfFile.Load(path);
}
