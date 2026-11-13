using System.Globalization;
using System.Text;
using AstroColour.Core.Curves;

namespace AstroColour.Core.Colour;

/// <summary>
/// Keeps recently made blurred selection masks, so that changing a colour's hue, saturation, lightness or curves
/// does not make its mask again. A mask is reused only for the same source pixels, the same edits before the colour
/// adjustments, the same selection and blur, and the same scale. Safe to share between threads.
/// </summary>
public sealed class MaskCache(long maxPixels = 120_000_000)
{
    /// <summary>What a mask depends on in its adjustment: the selection and the blur, but not the changes made.</summary>
    internal readonly record struct SelectionKey(float TargetHue, float Range, float Smoothness, float NeutralProtection,
        float LightnessMin, float LightnessMax, float LightnessSmoothness, float Blur)
    {
        public static SelectionKey Of(ColourAdjustment a) => new(a.TargetHue, a.Range, a.Smoothness, a.NeutralProtection,
            a.LightnessMin, a.LightnessMax, a.LightnessSmoothness, a.MaskBlur);
    }

    private readonly record struct Key(object Source, string BaseKey, SelectionKey Selection, float PixelScale);

    private readonly LinkedList<(Key Key, float[] Mask)> _entries = []; // most recently used first
    private readonly object _lock = new();
    private long _pixels;

    /// <summary>The number of masks held.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _pixels = 0;
        }
    }

    internal float[]? Find(object source, string baseKey, SelectionKey selection, float pixelScale)
    {
        var key = new Key(source, baseKey, selection, pixelScale);
        lock (_lock)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (!node.Value.Key.Equals(key)) continue;
                _entries.Remove(node);
                _entries.AddFirst(node);
                return node.Value.Mask;
            }
        }
        return null;
    }

    internal void Add(object source, string baseKey, SelectionKey selection, float pixelScale, float[] mask)
    {
        lock (_lock)
        {
            _entries.AddFirst((new Key(source, baseKey, selection, pixelScale), mask));
            _pixels += mask.Length;
            // Keep at least the newest mask, however large.
            while (_pixels > maxPixels && _entries.Count > 1)
            {
                _pixels -= _entries.Last!.Value.Mask.Length;
                _entries.RemoveLast();
            }
        }
    }

    /// <summary>A text key that changes whenever the edits made before the colour adjustments change.</summary>
    public static string BaseKey(NarrowbandNormalization? narrowband, CurveSet? curves, float saturation, float removePurple,
        HueCurveSet? hueCurves)
    {
        var sb = new StringBuilder();
        sb.Append(narrowband?.Settings.ToString() ?? "-").Append('|');
        sb.Append(saturation.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(removePurple.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        if (curves is not null)
            foreach (var channel in Enum.GetValues<CurveChannel>()) AppendPoints(sb, curves[channel].Points);
        sb.Append('|');
        if (hueCurves is not null)
        {
            AppendPoints(sb, hueCurves.Hue.Points);
            AppendPoints(sb, hueCurves.Saturation.Points);
        }
        return sb.ToString();
    }

    private static void AppendPoints(StringBuilder sb, List<CurvePoint> points)
    {
        foreach (var p in points)
            sb.Append(p.X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        sb.Append('/');
    }
}
