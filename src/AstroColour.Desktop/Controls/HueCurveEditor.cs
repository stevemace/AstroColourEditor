using System.Globalization;
using AstroColour.Core.Colour;
using AstroColour.Core.Curves;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AstroColour.Desktop.Controls;

/// <summary>What a <see cref="HueCurveEditor"/>'s vertical axis means.</summary>
public enum HueCurveKind
{
    /// <summary>Hue versus hue: the hue shift.</summary>
    Hue,
    /// <summary>Hue versus saturation: the saturation change.</summary>
    Saturation,
}

/// <summary>
/// An interactive graph of a <see cref="HueCurve"/>, with the hue circle along the bottom and a hue histogram behind.
/// Drag a point up or down to change that hue; click to add a point; double-click or right-click a point, or drag it
/// off the graph, to remove it.
/// </summary>
public sealed class HueCurveEditor : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<HueCurveEditor>();

    private const double Inset = 6;
    private const double BandHeight = 10;     // hue band below the plot
    private const double BandGap = 4;
    private const double HitRadius = 8;
    private const double PointRadius = 4.5;
    private const double RemoveDistance = 30;
    private const float MinGap = 0.01f;

    private HueCurve? _curve;
    private float[]? _histogram;
    private HueCurveKind _kind;

    private int _dragIndex = -1;
    private CurvePoint? _removedPoint;
    private Point? _hover;

    static HueCurveEditor()
    {
        AffectsRender<HueCurveEditor>(ForegroundProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The curve being edited, which this control changes in place.</summary>
    public HueCurve? Curve
    {
        get => _curve;
        set
        {
            EndDrag();
            _curve = value;
            InvalidateVisual();
        }
    }

    /// <summary>Hue histogram bin heights in [0,1], from 0° to 360°, drawn behind the curve.</summary>
    public float[]? Histogram
    {
        get => _histogram;
        set
        {
            _histogram = value;
            InvalidateVisual();
        }
    }

    public HueCurveKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the curve changes; the argument is true while a point is still being dragged.</summary>
    public event EventHandler<bool>? CurveChanged;

    private Rect Plot => new(Inset, Inset, Math.Max(1, Bounds.Width - 2 * Inset),
        Math.Max(1, Bounds.Height - 2 * Inset - BandHeight - BandGap));

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 256;
        return new Size(width, Math.Round(width * 0.6));
    }

    // --------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        var foreground = (Foreground as ISolidColorBrush)?.Color ?? Colors.White;

        // A transparent fill makes the whole area respond to the pointer.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.FillRectangle(new ImmutableSolidColorBrush(foreground, 0.04), plot);

        if (_histogram is { Length: > 0 } histogram)
        {
            for (int i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] <= 0) continue;
                double x0 = plot.X + plot.Width * i / histogram.Length;
                double x1 = plot.X + plot.Width * (i + 1) / histogram.Length;
                double height = plot.Height * 0.9 * histogram[i];
                context.FillRectangle(new ImmutableSolidColorBrush(Hsl((i + 0.5f) * 360f / histogram.Length, 0.8f, 0.5f), 0.3),
                    new Rect(x0, plot.Bottom - height, x1 - x0 + 0.5, height));
            }
        }

        // The hue circle along the bottom.
        var band = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        for (int i = 0; i <= 12; i++) band.GradientStops.Add(new GradientStop(Hsl(i * 30f, 1f, 0.5f), i / 12.0));
        context.FillRectangle(band, new Rect(plot.X, plot.Bottom + BandGap, plot.Width, BandHeight));

        var gridPen = new Pen(new ImmutableSolidColorBrush(foreground, 0.12), 1);
        for (int i = 1; i < 6; i++)
        {
            double x = Snap(plot.X + plot.Width * i / 6);
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        for (int i = 1; i < 4; i++)
        {
            if (i == 2) continue; // the neutral line is drawn below
            double y = Snap(plot.Y + plot.Height * i / 4);
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(foreground, 0.25), 1),
            new Rect(Snap(plot.X), Snap(plot.Y), Math.Round(plot.Width), Math.Round(plot.Height)));
        double middle = Snap(plot.Y + plot.Height / 2);
        context.DrawLine(new Pen(new ImmutableSolidColorBrush(foreground, 0.3), 1, new DashStyle([3, 3], 0)),
            new Point(plot.Left, middle), new Point(plot.Right, middle));

        if (_curve is null) return;

        int samples = Math.Max(2, (int)plot.Width);
        var values = _curve.Sample(samples);
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(ToScreen(0, values[0]), isFilled: false);
            for (int i = 1; i < samples; i++) g.LineTo(ToScreen(i / (float)(samples - 1), values[i]));
            g.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(foreground), 1.6, lineJoin: PenLineJoin.Round), line);

        var outline = new Pen(new ImmutableSolidColorBrush(foreground), 1.4);
        for (int i = 0; i < _curve.Points.Count; i++)
        {
            var p = _curve.Points[i];
            // Each point is filled with its hue; the one being dragged is drawn larger.
            double radius = i == _dragIndex ? PointRadius + 1.5 : PointRadius;
            context.DrawEllipse(new ImmutableSolidColorBrush(Hsl(p.X * 360f, 0.9f, 0.5f)), outline,
                ToScreen(p.X, p.Y), radius, radius);
        }

        DrawReadout(context, foreground);
    }

    private void DrawReadout(DrawingContext context, Color foreground)
    {
        float input, output;
        if (_dragIndex >= 0 && _curve is not null)
            (input, output) = (_curve.Points[_dragIndex].X, _curve.Points[_dragIndex].Y);
        else if (_removedPoint is null && _hover is { } hover && _curve is not null && Plot.Contains(hover))
        {
            input = (float)Math.Clamp((hover.X - Plot.X) / Plot.Width, 0, 1);
            output = _curve.Evaluate(input);
        }
        else
        {
            return;
        }

        float hue = input * 360f;
        string change = _kind == HueCurveKind.Hue
            ? $"Hue {Signed(HueCurveSet.HueShiftOf(output))}°"
            : $"Saturation {Signed(HueCurveSet.SaturationChangeOf(output))}%";
        var text = new FormattedText($"{ColourMath.HueName(hue)} {hue:0}°   {change}", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, new ImmutableSolidColorBrush(foreground, 0.75));
        context.DrawText(text, new Point(Plot.X + 6, Plot.Y + 4));
    }

    private static string Signed(float v)
    {
        double rounded = Math.Round(v);
        return rounded > 0 ? $"+{rounded:0}" : $"{rounded:0}";
    }

    private static Color Hsl(float h, float s, float l)
    {
        ColourMath.HslToRgb(h, s, l, out float r, out float g, out float b);
        return Color.FromRgb((byte)(r * 255 + 0.5f), (byte)(g * 255 + 0.5f), (byte)(b * 255 + 0.5f));
    }

    private Point ToScreen(float x, float y) => new(Plot.X + x * Plot.Width, Plot.Bottom - y * Plot.Height);

    private (float X, float Y) FromScreen(Point p) =>
        ((float)((p.X - Plot.X) / Plot.Width), (float)((Plot.Bottom - p.Y) / Plot.Height));

    private static double Snap(double v) => Math.Round(v) + 0.5;

    // ----------------------------------------------------------- interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_curve is null) return;
        var position = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        int hit = HitTest(position);

        if (properties.IsRightButtonPressed || (properties.IsLeftButtonPressed && e.ClickCount == 2))
        {
            if (hit >= 0)
            {
                _curve.Points.RemoveAt(hit);
                InvalidateVisual();
                CurveChanged?.Invoke(this, false);
            }
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;

        if (hit < 0)
        {
            if (!Plot.Inflate(Inset).Contains(position)) return;
            var (x, y) = FromScreen(position);
            hit = Insert(new CurvePoint(Math.Clamp(x, 0f, 1f - MinGap), Math.Clamp(y, 0f, 1f)));
            if (hit < 0) return;
            CurveChanged?.Invoke(this, true);
        }

        _dragIndex = hit;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _hover = position;
        if (_curve is null || (_dragIndex < 0 && _removedPoint is null))
        {
            Cursor = _curve is not null && HitTest(position) >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
            return;
        }

        var (x, y) = FromScreen(position);
        var point = new CurvePoint(Math.Clamp(x, 0f, 1f - MinGap), Math.Clamp(y, 0f, 1f));
        bool offGraph = !Plot.Inflate(RemoveDistance).Contains(position);

        if (offGraph && _removedPoint is null)
        {
            _removedPoint = _curve.Points[_dragIndex];
            _curve.Points.RemoveAt(_dragIndex);
            _dragIndex = -1;
        }
        else if (!offGraph && _removedPoint is not null)
        {
            _dragIndex = Insert(point);
            if (_dragIndex >= 0) _removedPoint = null;
        }
        else if (_dragIndex >= 0)
        {
            float low = _dragIndex > 0 ? _curve.Points[_dragIndex - 1].X + MinGap : 0f;
            float high = _dragIndex < _curve.Points.Count - 1 ? _curve.Points[_dragIndex + 1].X - MinGap : 1f - MinGap;
            _curve.Points[_dragIndex] = point with { X = Math.Clamp(point.X, low, Math.Max(low, high)) };
        }
        else
        {
            return;
        }

        InvalidateVisual();
        CurveChanged?.Invoke(this, true);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragIndex < 0 && _removedPoint is null) return;
        e.Pointer.Capture(null);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    private void EndDrag()
    {
        if (_dragIndex < 0 && _removedPoint is null) return;
        _dragIndex = -1;
        _removedPoint = null;
        InvalidateVisual();
        CurveChanged?.Invoke(this, false);
    }

    /// <summary>Inserts a point in X order, returning its index, or -1 if it is too close to an existing point.</summary>
    private int Insert(CurvePoint point)
    {
        var points = _curve!.Points;
        int index = points.FindIndex(p => p.X > point.X);
        if (index < 0) index = points.Count;
        if ((index > 0 && point.X - points[index - 1].X < MinGap) ||
            (index < points.Count && points[index].X - point.X < MinGap))
            return -1;
        points.Insert(index, point);
        return index;
    }

    /// <summary>The index of the point under <paramref name="position"/>, or -1.</summary>
    private int HitTest(Point position)
    {
        if (_curve is null) return -1;
        int best = -1;
        double bestDistance = HitRadius;
        for (int i = 0; i < _curve.Points.Count; i++)
        {
            var p = _curve.Points[i];
            var delta = ToScreen(p.X, p.Y) - position;
            double distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            if (distance <= bestDistance) (best, bestDistance) = (i, distance);
        }
        return best;
    }
}
