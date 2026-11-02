using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AstroColour.Desktop.Controls;

/// <summary>
/// Shows a crop rectangle over the image, which sits under it at the same size. While editing, the outside is dimmed
/// and the rectangle can be dragged to move it, its edges and corners dragged to resize it, or a new one drawn by
/// dragging outside it; otherwise the outside is hidden, so the image looks cropped. Coordinates are image pixels
/// as displayed (after rotation); <see cref="Zoom"/> converts them to this control's DIPs.
/// </summary>
public sealed class CropOverlay : Control
{
    private const double HandleHit = 9;      // DIPs from an edge or corner that grab it
    private const double HandleSize = 7;
    private const double MinScreenSize = 12; // smallest rectangle, in DIPs

    [Flags]
    private enum Grip { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private Rect _selection;
    private Size _imageSize;
    private double _zoom = 1;
    private bool _isEditing;
    private double? _aspectRatio;

    private Grip _grip;
    private Point _dragStart;           // in image pixels
    private Rect _dragStartSelection;
    private Point? _anchor;             // the fixed corner while drawing or resizing from a corner

    /// <summary>Raised when the user changes the selection; the argument is true while still dragging.</summary>
    public event EventHandler<bool>? SelectionChanged;

    /// <summary>The image's size as displayed, in pixels.</summary>
    public Size ImageSize
    {
        get => _imageSize;
        set { _imageSize = value; InvalidateVisual(); }
    }

    /// <summary>DIPs per image pixel.</summary>
    public double Zoom
    {
        get => _zoom;
        set { _zoom = value; InvalidateVisual(); }
    }

    /// <summary>The rectangle kept, in displayed image pixels.</summary>
    public Rect Selection
    {
        get => _selection;
        set { _selection = value; InvalidateVisual(); }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set { _isEditing = value; IsHitTestVisible = value; InvalidateVisual(); }
    }

    /// <summary>Width ÷ height the selection keeps, or null for any shape.</summary>
    public double? AspectRatio
    {
        get => _aspectRatio;
        set => _aspectRatio = value is > 0 ? value : null;
    }

    /// <summary>
    /// Reshapes the selection to <see cref="AspectRatio"/> about its centre, keeping roughly its area and fitting it in
    /// the image.
    /// </summary>
    public void ApplyAspectRatio()
    {
        if (_aspectRatio is not { } ratio || _imageSize.Width <= 0) return;
        var centre = _selection.Center;
        double area = Math.Max(1, _selection.Width * _selection.Height);
        double w = Math.Sqrt(area * ratio), h = w / ratio;
        double scale = Math.Min(1, Math.Min(_imageSize.Width / w, _imageSize.Height / h));
        w *= scale;
        h *= scale;
        _selection = Place(centre.X - w / 2, centre.Y - h / 2, w, h);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, false);
    }

    // --------------------------------------------------------------- drawing

    public override void Render(DrawingContext context)
    {
        if (_imageSize.Width <= 0) return;
        var image = new Rect(0, 0, _imageSize.Width * _zoom, _imageSize.Height * _zoom);
        var sel = ToScreen(_selection);

        // Outside the selection: dimmed while editing, hidden (in the viewer's background colour) otherwise.
        var outside = new ImmutableSolidColorBrush(_isEditing ? Color.FromArgb(0x99, 0, 0, 0) : Color.FromRgb(0x14, 0x14, 0x14));
        context.FillRectangle(outside, new Rect(image.Left, image.Top, image.Width, Math.Max(0, sel.Top - image.Top)));
        context.FillRectangle(outside, new Rect(image.Left, sel.Bottom, image.Width, Math.Max(0, image.Bottom - sel.Bottom)));
        context.FillRectangle(outside, new Rect(image.Left, sel.Top, Math.Max(0, sel.Left - image.Left), sel.Height));
        context.FillRectangle(outside, new Rect(sel.Right, sel.Top, Math.Max(0, image.Right - sel.Right), sel.Height));

        if (!_isEditing) return;

        // A transparent fill makes the whole image area respond to the pointer.
        context.FillRectangle(Brushes.Transparent, image);
        var thirds = new Pen(new ImmutableSolidColorBrush(Colors.White, 0.35), 1);
        for (int i = 1; i < 3; i++)
        {
            double x = sel.Left + sel.Width * i / 3, y = sel.Top + sel.Height * i / 3;
            context.DrawLine(thirds, new Point(x, sel.Top), new Point(x, sel.Bottom));
            context.DrawLine(thirds, new Point(sel.Left, y), new Point(sel.Right, y));
        }
        context.DrawRectangle(new Pen(Brushes.White, 1.5), sel);

        var fill = Brushes.White;
        var edge = new Pen(new ImmutableSolidColorBrush(Colors.Black, 0.6), 1);
        foreach (var p in new[]
                 {
                     sel.TopLeft, sel.TopRight, sel.BottomLeft, sel.BottomRight,
                     new Point(sel.Center.X, sel.Top), new Point(sel.Center.X, sel.Bottom),
                     new Point(sel.Left, sel.Center.Y), new Point(sel.Right, sel.Center.Y),
                 })
            context.DrawRectangle(fill, edge, new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize));
    }

    private Rect ToScreen(Rect r) => new(r.X * _zoom, r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);

    private Point ToImage(Point p) =>
        new(Math.Clamp(p.X / _zoom, 0, _imageSize.Width), Math.Clamp(p.Y / _zoom, 0, _imageSize.Height));

    // ----------------------------------------------------------- interaction

    private Grip HitTest(Point screen)
    {
        var sel = ToScreen(_selection);
        if (!sel.Inflate(HandleHit).Contains(screen)) return Grip.None;
        var grip = Grip.None;
        if (Math.Abs(screen.X - sel.Left) <= HandleHit) grip |= Grip.Left;
        else if (Math.Abs(screen.X - sel.Right) <= HandleHit) grip |= Grip.Right;
        if (Math.Abs(screen.Y - sel.Top) <= HandleHit) grip |= Grip.Top;
        else if (Math.Abs(screen.Y - sel.Bottom) <= HandleHit) grip |= Grip.Bottom;
        if (grip == Grip.None && sel.Contains(screen)) grip = Grip.Move;
        return grip;
    }

    private static Cursor CursorFor(Grip grip) => new(grip switch
    {
        Grip.Left | Grip.Top or Grip.Right | Grip.Bottom => StandardCursorType.TopLeftCorner,
        Grip.Right | Grip.Top or Grip.Left | Grip.Bottom => StandardCursorType.TopRightCorner,
        Grip.Left or Grip.Right => StandardCursorType.SizeWestEast,
        Grip.Top or Grip.Bottom => StandardCursorType.SizeNorthSouth,
        Grip.Move => StandardCursorType.SizeAll,
        _ => StandardCursorType.Cross,
    });

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_isEditing || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var screen = e.GetPosition(this);
        _grip = HitTest(screen);
        _dragStart = ToImage(screen);
        _dragStartSelection = _selection;
        _anchor = _grip switch
        {
            // Drawing a new rectangle, or resizing from a corner: the opposite corner stays put.
            Grip.None => _dragStart,
            _ when IsCorner(_grip) => new Point(
                _grip.HasFlag(Grip.Left) ? _selection.Right : _selection.Left,
                _grip.HasFlag(Grip.Top) ? _selection.Bottom : _selection.Top),
            _ => null,
        };
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isEditing) return;
        var screen = e.GetPosition(this);
        if (!ReferenceEquals(e.Pointer.Captured, this))
        {
            Cursor = CursorFor(HitTest(screen));
            return;
        }

        var p = ToImage(screen);
        if (_grip == Grip.Move)
        {
            var d = p - _dragStart;
            _selection = Place(_dragStartSelection.X + d.X, _dragStartSelection.Y + d.Y, _dragStartSelection.Width, _dragStartSelection.Height);
        }
        else if (_anchor is { } anchor)
        {
            _selection = FromAnchor(anchor, p);
        }
        else
        {
            _selection = FromEdge(p);
        }
        InvalidateVisual();
        SelectionChanged?.Invoke(this, true);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!ReferenceEquals(e.Pointer.Captured, this)) return;
        e.Pointer.Capture(null);
        EndDrag();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_grip == Grip.None && _anchor is null) return;
        bool moved = _selection != _dragStartSelection;
        _grip = Grip.None;
        _anchor = null;
        if (moved) SelectionChanged?.Invoke(this, false);
    }

    private static bool IsCorner(Grip grip) =>
        (grip & (Grip.Left | Grip.Right)) != 0 && (grip & (Grip.Top | Grip.Bottom)) != 0;

    private double MinSize => Math.Max(1, MinScreenSize / _zoom);

    /// <summary>A rectangle from a fixed corner to the pointer, keeping the aspect ratio if one is set.</summary>
    private Rect FromAnchor(Point anchor, Point p)
    {
        double sx = p.X >= anchor.X ? 1 : -1, sy = p.Y >= anchor.Y ? 1 : -1;
        double maxW = sx > 0 ? _imageSize.Width - anchor.X : anchor.X;
        double maxH = sy > 0 ? _imageSize.Height - anchor.Y : anchor.Y;
        double w = Math.Abs(p.X - anchor.X), h = Math.Abs(p.Y - anchor.Y);
        if (_aspectRatio is { } r)
        {
            w = Math.Max(w, h * r);
            h = w / r;
            if (w > maxW) (w, h) = (maxW, maxW / r);
            if (h > maxH) (w, h) = (maxH * r, maxH);
        }
        w = Math.Clamp(w, Math.Min(MinSize, maxW), maxW);
        h = Math.Clamp(h, Math.Min(MinSize, maxH), maxH);
        return new Rect(sx > 0 ? anchor.X : anchor.X - w, sy > 0 ? anchor.Y : anchor.Y - h, w, h);
    }

    /// <summary>The selection with one edge moved to the pointer; with an aspect ratio, the other side follows about its centre.</summary>
    private Rect FromEdge(Point p)
    {
        var s = _dragStartSelection;
        double left = s.Left, top = s.Top, right = s.Right, bottom = s.Bottom, min = MinSize;
        if (_grip.HasFlag(Grip.Left)) left = Math.Min(p.X, right - min);
        if (_grip.HasFlag(Grip.Right)) right = Math.Max(p.X, left + min);
        if (_grip.HasFlag(Grip.Top)) top = Math.Min(p.Y, bottom - min);
        if (_grip.HasFlag(Grip.Bottom)) bottom = Math.Max(p.Y, top + min);
        if (_aspectRatio is not { } r) return new Rect(left, top, right - left, bottom - top);

        if (_grip is Grip.Left or Grip.Right)
        {
            double w = right - left, h = Math.Min(w / r, _imageSize.Height);
            w = h * r;
            if (_grip == Grip.Left) left = right - w;
            return Place(left, s.Center.Y - h / 2, w, h, keepX: true);
        }
        else
        {
            double h = bottom - top, w = Math.Min(h * r, _imageSize.Width);
            h = w / r;
            if (_grip == Grip.Top) top = bottom - h;
            return Place(s.Center.X - w / 2, top, w, h, keepY: true);
        }
    }

    /// <summary>A rectangle of the given size moved as little as needed to lie within the image.</summary>
    private Rect Place(double x, double y, double w, double h, bool keepX = false, bool keepY = false)
    {
        w = Math.Min(w, _imageSize.Width);
        h = Math.Min(h, _imageSize.Height);
        if (!keepX) x = Math.Clamp(x, 0, _imageSize.Width - w);
        if (!keepY) y = Math.Clamp(y, 0, _imageSize.Height - h);
        return new Rect(x, y, w, h);
    }
}
