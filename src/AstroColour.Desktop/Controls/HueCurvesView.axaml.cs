using AstroColour.Core.Curves;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AstroColour.Desktop.Controls;

/// <summary>Hue/saturation buttons, a <see cref="HueCurveEditor"/> and reset links for editing a <see cref="HueCurveSet"/>.</summary>
public partial class HueCurvesView : UserControl
{
    private HueCurveSet? _curves;
    private HueCurveKind _kind = HueCurveKind.Hue;

    public HueCurvesView()
    {
        InitializeComponent();
        ShowKind(HueCurveKind.Hue);
    }

    /// <summary>Raised when the curves change; the argument is true while a point is still being dragged.</summary>
    public event EventHandler<bool>? CurvesChanged;

    /// <summary>The curves being edited, which this control changes in place.</summary>
    public HueCurveSet? Curves
    {
        get => _curves;
        set
        {
            _curves = value;
            ShowKind(_kind);
        }
    }

    /// <summary>Hue histogram, as from <see cref="HueCurveSet.Histogram"/>.</summary>
    public float[]? Histogram
    {
        get => Graph.Histogram;
        set => Graph.Histogram = value;
    }

    public string? Hint
    {
        get => HintText.Text;
        set => HintText.Text = value;
    }

    /// <summary>Redraws after the curves were changed from outside, e.g. by undo.</summary>
    public void Refresh() => ShowKind(_kind);

    private HueCurve? Current => _curves is null ? null : _kind == HueCurveKind.Hue ? _curves.Hue : _curves.Saturation;

    private void ShowKind(HueCurveKind kind)
    {
        _kind = kind;
        HueButton.IsChecked = kind == HueCurveKind.Hue;
        SaturationButton.IsChecked = kind == HueCurveKind.Saturation;
        Graph.Kind = kind;
        Graph.Curve = Current;
        KindText.Text = kind == HueCurveKind.Hue ? "Hue vs hue" : "Hue vs saturation";
    }

    private void Kind_Click(object? sender, RoutedEventArgs e) =>
        ShowKind(sender == SaturationButton ? HueCurveKind.Saturation : HueCurveKind.Hue);

    private void Graph_CurveChanged(object? sender, bool interactive) => CurvesChanged?.Invoke(this, interactive);

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is not { } curve) return;
        curve.Reset();
        Graph.InvalidateVisual();
        CurvesChanged?.Invoke(this, false);
    }

    private void ResetAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_curves is null) return;
        _curves.Reset();
        Graph.InvalidateVisual();
        CurvesChanged?.Invoke(this, false);
    }
}
