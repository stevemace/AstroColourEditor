using System.Globalization;
using AstroColour.Core.Curves;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AstroColour.Desktop.Controls;

/// <summary>
/// An interactive graph of a <see cref="ToneCurve"/> over the image histogram. Click to add a point and
/// drag to move it; double-click or right-click a point, or drag it off the graph, to remove it.
/// </summary>
public sealed class CurveEditor : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CurveEditor>();

    private const double Inset = 6;          // room for points drawn on the edges
    private const double HitRadius = 8;
    private const double PointRadius = 4.5;
    private const double RemoveDistance = 30; // how far off the graph a point must be dragged to remove it
    private const float MinGap = 0.01f;       // minimum horizontal spacing of points

    private ToneCurve? _curve;
    private float[]? _histogram;
    private Color? _curveColour;

    private int _dragIndex = -1;
    private CurvePoint? _removedPoint;        // the point being dragged while it is off the graph
    private Point? _hover;

    static CurveEditor()
    {
        AffectsRender<CurveEditor>(ForegroundProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The curve being edited, which this control changes in place.</summary>
    public ToneCurve? Curve
    {
        get => _curve;
        set
        {
            EndDrag();
            _curve = value;
            InvalidateVisual();
        }
    }

    /// <summary>Histogram bin heights in [0,1], drawn behind the curve.</summary>
    public float[]? Histogram
    {
        get => _histogram;
        set
        {
            _histogram = value;
            InvalidateVisual();
        }
    }

    /// <summary>The colour of the curve and its histogram; the foreground colour if null.</summary>
    public Color? CurveColour
    {
        get => _curveColour;
        set
        {
            _curveColour = value;
            InvalidateVisual();
        }
    }

    /// <summary>Raised when the curve changes; the argument is true while a point is still being dragged.</summary>
    public event EventHandler<bool>? CurveChanged;

    private Rect Plot => new Rect(Bounds.Size).Deflate(Inset);

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = double.IsFinite(availableSize.Width) ? availableSize.Width : 256;
        return new Size(side, side);
    }

    // --------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        var foreground = (Foreground as ISolidColorBrush)?.Color ?? Colors.White;
        var colour = _curveColour ?? foreground;

        // A transparent fill makes the whole area respond to the pointer.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.FillRectangle(new ImmutableSolidColorBrush(foreground, 0.04), plot);

        if (_histogram is { Length: > 1 } histogram)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(plot.BottomLeft, isFilled: true);
                for (int i = 0; i < histogram.Length; i++)
                {
                    double x0 = plot.X + plot.Width * i / histogram.Length;
                    double x1 = plot.X + plot.Width * (i + 1) / histogram.Length;
                    double y = plot.Bottom - plot.Height * 0.9 * histogram[i];
                    g.LineTo(new Point(x0, y));
                    g.LineTo(new Point(x1, y));
                }
                g.LineTo(plot.BottomRight);
                g.EndFigure(isClosed: true);
            }
            context.DrawGeometry(new ImmutableSolidColorBrush(colour, 0.18), null, geometry);
        }

        var gridPen = new Pen(new ImmutableSolidColorBrush(foreground, 0.12), 1);
        for (int i = 1; i < 4; i++)
        {
            double x = Snap(plot.X + plot.Width * i / 4), y = Snap(plot.Y + plot.Height * i / 4);
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(foreground, 0.25), 1),
            new Rect(Snap(plot.X), Snap(plot.Y), Math.Round(plot.Width), Math.Round(plot.Height)));
        context.DrawLine(new Pen(new ImmutableSolidColorBrush(foreground, 0.2), 1, new DashStyle([3, 3], 0)),
            plot.BottomLeft, plot.TopRight);

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
        context.DrawGeometry(null, new Pen(new ImmutableSolidColorBrush(colour), 1.6, lineJoin: PenLineJoin.Round), line);

        var pointPen = new Pen(new ImmutableSolidColorBrush(colour), 1.4);
        var pointFill = new ImmutableSolidColorBrush(colour);
        // Unselected points are hollow: filled with a colour that contrasts with the foreground, as the panel does.
        bool lightText = foreground.R + foreground.G + foreground.B > 3 * 128;
        var hollow = new ImmutableSolidColorBrush(lightText ? Color.FromRgb(0x26, 0x26, 0x26) : Color.FromRgb(0xF7, 0xF7, 0xF7));
        for (int i = 0; i < _curve.Points.Count; i++)
        {
            var p = _curve.Points[i];
            context.DrawEllipse(i == _dragIndex ? pointFill : hollow, pointPen, ToScreen(p.X, p.Y), PointRadius, PointRadius);
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

        var text = new FormattedText($"In {input * 100:0.0}%   Out {output * 100:0.0}%", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, new ImmutableSolidColorBrush(foreground, 0.75));
        context.DrawText(text, new Point(Plot.X + 6, Plot.Y + 4));
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
            if (hit >= 0 && _curve.Points.Count > 2)
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
            hit = Insert(new CurvePoint(Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f)));
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
        var point = new CurvePoint(Math.Clamp(x, 0f, 1f), Math.Clamp(y, 0f, 1f));
        bool offGraph = !Plot.Inflate(RemoveDistance).Contains(position);

        if (offGraph && _removedPoint is null && _curve.Points.Count > 2)
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
            float high = _dragIndex < _curve.Points.Count - 1 ? _curve.Points[_dragIndex + 1].X - MinGap : 1f;
            _curve.Points[_dragIndex] = point with { X = Math.Clamp(point.X, low, high) };
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
