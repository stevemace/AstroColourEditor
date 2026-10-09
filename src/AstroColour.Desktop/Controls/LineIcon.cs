using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AstroColour.Desktop.Controls;

/// <summary>
/// A line-drawn icon, 16×16 unless sized otherwise, stroked in the inherited text colour so it follows
/// the theme and dims with its button when disabled.
/// </summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.4);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<LineIcon>();

    static LineIcon()
    {
        AffectsRender<LineIcon>(DataProperty, StrokeThicknessProperty, ForegroundProperty);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        if (Data is { } geometry && Foreground is { } brush)
            context.DrawGeometry(null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
    }
}
