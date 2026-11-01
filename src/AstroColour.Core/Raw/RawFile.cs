using System.Globalization;
using AstroColour.Core.Fits;
using Sdcb.LibRaw;

namespace AstroColour.Core.Raw;

/// <summary>
/// A camera raw file (Nikon NEF, Canon CR2/CR3, Sony ARW, …) developed with LibRaw like a photo: demosaiced
/// with the camera's white balance into 16-bit sRGB. Highlights are not brightened into clipping, so stars
/// keep their colour. Raw files cannot be written, so the image is always saved in another format.
/// </summary>
public sealed class RawFile : IAstroImage
{
    private RawFile(int width, int height, float[][] planes, IReadOnlyList<FitsKeyword> keywords)
    {
        Width = width;
        Height = height;
        Planes = planes;
        FitsKeywords = keywords;
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels => 3;
    public string ColorSpace => "RGB";
    public string SampleFormatName => "UInt16";
    public int BitsPerSample => 16;
    public bool IsFloat => false;
    public IReadOnlyList<FitsKeyword> FitsKeywords { get; }
    public float[][] Planes { get; }

    public static RawFile Load(string path)
    {
        RawContext raw;
        try
        {
            raw = RawContext.OpenFile(path);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new NotSupportedException("Camera raw files need the LibRaw library, which is not available on this system.", ex);
        }

        using (raw)
        {
            raw.Unpack();
            raw.DcrawProcess(o =>
            {
                o.OutputBps = 16;
                o.UseCameraWb = true;
                o.OutputColor = LibRawColorSpace.SRGB;
                // The sRGB curve rather than LibRaw's default BT.709; LibRaw derives the other four values.
                var gamma = o.Gamma;
                (gamma[0], gamma[1]) = (1 / 2.4, 12.92);
                o.Gamma = gamma;
                o.NoAutoBright = true;
            });
            using var image = raw.MakeDcrawMemoryImage();
            if (image.Channels != 3 || image.Bits != 16)
                throw new InvalidDataException($"Unexpected developed raw image: {image.Channels} channels of {image.Bits} bits.");

            int width = image.Width, height = image.Height, count = width * height;
            var planes = new[] { new float[count], new float[count], new float[count] };
            var data = image.AsSpan<ushort>();
            for (int i = 0; i < count; i++)
            {
                planes[0][i] = data[3 * i] / 65535f;
                planes[1][i] = data[3 * i + 1] / 65535f;
                planes[2][i] = data[3 * i + 2] / 65535f;
            }
            return new RawFile(width, height, planes, Keywords(raw));
        }
    }

    /// <summary>The camera, exposure and capture time, as the FITS keywords capture software writes.</summary>
    private static List<FitsKeyword> Keywords(RawContext raw)
    {
        var keywords = new List<FitsKeyword>();
        var camera = raw.ImageParams;
        var shot = raw.ImageOtherParams;
        string model = string.Join(' ', new[] { camera.Make, camera.Model }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));

        if (model.Length > 0) keywords.Add(new("INSTRUME", Quote(model), "Camera"));
        if (shot.Shutter > 0) keywords.Add(new("EXPTIME", Number(shot.Shutter), "[s] Exposure time"));
        if (shot.IsoSpeed > 0) keywords.Add(new("ISOSPEED", Number(shot.IsoSpeed), "ISO speed"));
        if (shot.FocalLength > 0) keywords.Add(new("FOCALLEN", Number(shot.FocalLength), "[mm] Focal length"));
        if (shot.Aperture > 0) keywords.Add(new("FNUMBER", Number(shot.Aperture), "Focal ratio"));
        if (shot.Timestamp > 0)
        {
            // Cameras record their own clock without a time zone, so this is local time rather than DATE-OBS.
            var local = DateTimeOffset.FromUnixTimeSeconds(shot.Timestamp).ToLocalTime().DateTime;
            keywords.Add(new("DATE-LOC", Quote(local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)), "Camera clock time"));
        }
        return keywords;
    }

    private static string Quote(string s) => $"'{s.Replace("'", "''")}'";

    private static string Number(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    public void Save(string path, float[][]? planes = null, Rotation rotation = Rotation.None) =>
        throw new NotSupportedException("Camera raw files cannot be written. Save as XISF, FITS, TIFF, PNG or JPEG.");
}
