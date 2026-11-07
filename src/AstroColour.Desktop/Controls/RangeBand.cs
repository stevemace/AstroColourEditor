using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AstroColour.Desktop.Controls;

/// <summary>
/// Edits a selected range drawn over a colour ramp: fully selected between <see cref="Low"/> and <see cref="High"/>,
/// fading out over a further <see cref="Feather"/> on each side. Drag the top corners to move an edge, the bottom
/// corners to change the feather, or the middle to move the whole range; click elsewhere to centre it there, and
/// double-click to reset. With <see cref="IsCircular"/> (hues) the range wraps around the ends of the ramp, so
/// <see cref="Low"/> and <see cref="High"/> may fall outside [0, <see cref="Maximum"/>].
/// </summary>
public sealed class RangeBand : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<RangeBand>();

    private const double Inset = 7;
    private const double PlotHeight = 40;
    private const double RampHeight = 10;
    private const double RampGap = 3;
    private const double HitRadius = 9;
    private const double HandleRadius = 4;

    private enum Part { None, Low, High, LowFeather, HighFeather, Body }

    private double _low, _high = 1, _feather;
    private float[]? _histogram;
    private IBrush? _ramp;
    private Func<double, Color>? _histogramColour;

    private Part _drag = Part.None;
    private double _grabOffset; // value under the pointer minus Low, when dragging the body
    private bool _changed;      // whether the current drag has changed the range

    static RangeBand()
    {
        AffectsRender<RangeBand>(ForegroundProperty);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The value at the right end of the ramp; the left end is 0.</summary>
    public double Maximum { get; set; } = 100;

    /// <summary>Whether the values wrap around, as hues do.</summary>
    public bool IsCircular { get; set; }

    /// <summary>The narrowest the fully selected part can be.</summary>
    public double MinSpan { get; set; }

    /// <summary>The widest the fully selected part can be.</summary>
    public double MaxSpan { get; set; } = double.PositiveInfinity;

    public double MaxFeather { get; set; } = double.PositiveInfinity;

    /// <summary>The colours along the ramp, from 0 to <see cref="Maximum"/>.</summary>
    public IBrush? Ramp
    {
        get => _ramp;
        set
        {
            _ramp = value;
            InvalidateVisual();
        }
    }

    /// <summary>Bin heights in [0,1] across the ramp, drawn behind the range.</summary>
    public float[]? Histogram
    {
        get => _histogram;
        set
        {
            _histogram = value;
            InvalidateVisual();
        }
    }

    /// <summary>The colour of the histogram bar at a value, or null for the foreground colour.</summary>
    public Func<double, Color>? HistogramColour
    {
        get => _histogramColour;
        set
        {
            _histogramColour = value;
            InvalidateVisual();
        }
    }

    public double Low => _low;

    public double High => _high;

    public double Feather => _feather;

    /// <summary>Raised when the user changes the range; the argument is true while it is still being dragged.</summary>
    public event EventHandler<bool>? RangeChanged;

    /// <summary>Raised when the user starts to drag the range.</summary>
    public event EventHandler? DragStarted;

    /// <summary>Raised when the user lets go of the range, whether or not it changed.</summary>
    public event EventHandler? DragCompleted;

    /// <summary>Raised on a double-click, for the owner to put the range back to its defaults.</summary>
    public event EventHandler? ResetRequested;

    /// <summary>Shows a range, without raising <see cref="RangeChanged"/>.</summary>
    public void SetRange(double low, double high, double feather)
    {
        (_low, _high, _feather) = (low, high, feather);
        InvalidateVisual();
    }

    public bool IsDragging => _drag != Part.None;

    private Rect Plot => new(Inset, Inset, Math.Max(1, Bounds.Width - 2 * Inset), PlotHeight);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : 256;
        return new Size(width, 2 * Inset + PlotHeight + RampGap + RampHeight);
    }

    // --------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        var plot = Plot;
        var foreground = (Foreground as ISolidColorBrush)?.Color ?? Colors.White;

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
                var colour = _histogramColour?.Invoke((i + 0.5) * Maximum / histogram.Length) ?? foreground;
                context.FillRectangle(new ImmutableSolidColorBrush(colour, 0.28),
                    new Rect(x0, plot.Bottom - height, x1 - x0 + 0.5, height));
            }
        }

        if (_ramp is not null)
            context.FillRectangle(_ramp, new Rect(plot.X, plot.Bottom + RampGap, plot.Width, RampHeight), 2f);

        // The selection weight across the ramp, as a filled outline.
        double top = plot.Y + 3;
        int samples = Math.Max(2, (int)plot.Width + 1);
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(plot.X, plot.Bottom), isFilled: true);
            for (int i = 0; i < samples; i++)
            {
                double x = plot.X + plot.Width * i / (samples - 1);
                double w = WeightAt(Maximum * i / (samples - 1));
                g.LineTo(new Point(x, plot.Bottom - w * (plot.Bottom - top)));
            }
            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(isClosed: true);
        }
        context.DrawGeometry(new ImmutableSolidColorBrush(foreground, 0.16),
            new Pen(new ImmutableSolidColorBrush(foreground, 0.45), 1.4, lineJoin: PenLineJoin.Round), shape);
        context.DrawRectangle(new Pen(new ImmutableSolidColorBrush(foreground, 0.25), 1),
            new Rect(Math.Round(plot.X) + 0.5, Math.Round(plot.Y) + 0.5, Math.Round(plot.Width), Math.Round(plot.Height)));

        // Grey rather than the full foreground colour, which is harsh against the ramp.
        var fill = new ImmutableSolidColorBrush(foreground, 0.5);
        var outline = new Pen(new ImmutableSolidColorBrush(foreground, 0.25), 1);
        foreach (var part in new[] { Part.Low, Part.High, Part.LowFeather, Part.HighFeather })
        {
            double radius = part == _drag ? HandleRadius + 1.5 : HandleRadius;
            foreach (var p in HandlePoints(part))
                context.DrawEllipse(fill, outline, p, radius, radius);
        }
    }

    /// <summary>The selection weight, 0..1, at a value: the same smooth falloff the colour editor uses.</summary>
    private double WeightAt(double v)
    {
        double centre = (_low + _high) / 2, half = (_high - _low) / 2;
        double distance = Math.Abs(v - centre);
        if (IsCircular)
        {
            distance %= Maximum;
            if (distance > Maximum / 2) distance = Maximum - distance;
        }
        if (distance <= half) return 1;
        if (_feather <= 0 || distance >= half + _feather) return 0;
        double t = (distance - half) / _feather;
        return 1 - t * t * (3 - 2 * t);
    }

    private double ValueOfHandle(Part part) => part switch
    {
        Part.Low => _low,
        Part.High => _high,
        Part.LowFeather => _low - _feather,
        Part.HighFeather => _high + _feather,
        _ => (_low + _high) / 2,
    };

    /// <summary>Where a handle is drawn: once, or for a circular range, at each place it shows on the ramp.</summary>
    private IEnumerable<Point> HandlePoints(Part part)
    {
        var plot = Plot;
        double y = part is Part.Low or Part.High ? plot.Y + 3 : plot.Bottom;
        double v = ValueOfHandle(part);
        if (!IsCircular)
        {
            if (v >= 0 && v <= Maximum) yield return new Point(ToX(v), y);
            yield break;
        }
        v = Wrap(v);
        yield return new Point(ToX(v), y);
        // A handle at an end of the ramp also shows at the other end.
        if (v < Maximum * 0.02) yield return new Point(ToX(v + Maximum), y);
    }

    private double ToX(double v) => Plot.X + Plot.Width * v / Maximum;

    private double ToValue(double x) => (x - Plot.X) / Plot.Width * Maximum;

    private double Wrap(double v)
    {
        v %= Maximum;
        return v < 0 ? v + Maximum : v;
    }

    /// <summary>For a circular range, <paramref name="v"/> moved by whole turns to lie nearest <paramref name="near"/>.</summary>
    private double Unwrap(double v, double near)
    {
        if (!IsCircular) return v;
        double d = (v - near) % Maximum;
        if (d > Maximum / 2) d -= Maximum;
        else if (d < -Maximum / 2) d += Maximum;
        return near + d;
    }

    // ----------------------------------------------------------- interaction

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed) return;
        var position = e.GetPosition(this);
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            EndDrag();
            ResetRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var part = HitTest(position);
        double v = ToValue(position.X);
        if (part == Part.None)
        {
            // Centre the range where the click was, then carry on as a drag of the whole range.
            double half = (_high - _low) / 2;
            _low = v - half;
            _high = v + half;
            ClampMove();
            part = Part.Body;
            _changed = true;
        }
        else
        {
            _changed = false;
        }
        if (part == Part.Body) _grabOffset = Unwrap(v, _low) - _low;

        _drag = part;
        e.Pointer.Capture(this);
        DragStarted?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        if (_changed) RangeChanged?.Invoke(this, true);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_drag == Part.None)
        {
            Cursor = HitTest(position) switch
            {
                Part.None => Cursor.Default,
                Part.Body => new Cursor(StandardCursorType.SizeAll),
                _ => new Cursor(StandardCursorType.SizeWestEast),
            };
            return;
        }

        double v = ToValue(position.X);
        switch (_drag)
        {
            case Part.Body:
                double width = _high - _low;
                _low = Unwrap(v, _low + _grabOffset) - _grabOffset;
                _high = _low + width;
                ClampMove();
                break;
            case Part.Low:
                _low = _high - Math.Clamp(_high - Unwrap(v, _low), MinSpan, MaxSpan);
                if (!IsCircular && _low < 0) _low = 0;
                break;
            case Part.High:
                _high = _low + Math.Clamp(Unwrap(v, _high) - _low, MinSpan, MaxSpan);
                if (!IsCircular && _high > Maximum) _high = Maximum;
                break;
            case Part.LowFeather:
                _feather = Math.Clamp(_low - Unwrap(v, _low - _feather), 0, MaxFeather);
                break;
            case Part.HighFeather:
                _feather = Math.Clamp(Unwrap(v, _high + _feather) - _high, 0, MaxFeather);
                break;
        }
        _changed = true;
        InvalidateVisual();
        RangeChanged?.Invoke(this, true);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Part.None) return;
        e.Pointer.Capture(null);
        EndDrag();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_drag == Part.None) return;
        _drag = Part.None;
        InvalidateVisual();
        if (_changed) RangeChanged?.Invoke(this, false);
        _changed = false;
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keeps a moved range on the ramp; a circular one is only brought back to a single turn.</summary>
    private void ClampMove()
    {
        double width = _high - _low;
        if (IsCircular)
        {
            double centre = (_low + _high) / 2, wrapped = Wrap(centre);
            _low += wrapped - centre;
            _high = _low + width;
            return;
        }
        _low = Math.Clamp(_low, 0, Math.Max(0, Maximum - width));
        _high = _low + width;
    }

    private Part HitTest(Point position)
    {
        var best = Part.None;
        double bestDistance = HitRadius;
        // The edges come first, so when there is no feather they win over the feather handles beneath them.
        foreach (var part in new[] { Part.Low, Part.High, Part.LowFeather, Part.HighFeather })
            foreach (var p in HandlePoints(part))
            {
                var d = p - position;
                double distance = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                if (distance < bestDistance) (best, bestDistance) = (part, distance);
            }
        if (best != Part.None) return best;

        var plot = Plot;
        if (position.Y < plot.Y - Inset || position.Y > plot.Bottom + RampGap + RampHeight + Inset) return Part.None;
        return WeightAt(ToValue(position.X)) > 0 ? Part.Body : Part.None;
    }
}
