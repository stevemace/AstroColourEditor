using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AstroColour.Desktop.Controls;

/// <summary>A slider with a label, a live value readout, an optional colour ramp and double-click reset.</summary>
public partial class LabeledSlider : UserControl
{
    public LabeledSlider()
    {
        InitializeComponent();
        UpdateValueText();
    }

    public event EventHandler? ValueChanged;

    public string? Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value;
    }

    public double Minimum
    {
        get => Slider.Minimum;
        set => Slider.Minimum = value;
    }

    public double Maximum
    {
        get => Slider.Maximum;
        set => Slider.Maximum = value;
    }

    public double Value
    {
        get => Slider.Value;
        set => Slider.Value = value;
    }

    public double DefaultValue { get; set; }

    /// <summary>The increment the slider snaps to.</summary>
    public double Step
    {
        get => Slider.TickFrequency;
        set => Slider.TickFrequency = value;
    }

    /// <summary>Decimal places shown in the value readout.</summary>
    public int Decimals
    {
        get => _decimals;
        set
        {
            _decimals = value;
            UpdateValueText();
        }
    }

    private int _decimals;

    public string Unit { get; set; } = "";

    /// <summary>Show a leading "+" for positive values (for relative adjustments).</summary>
    public bool ShowSign { get; set; }

    public IBrush? GradientBrush
    {
        get => Gradient.Fill;
        set
        {
            Gradient.Fill = value;
            Gradient.IsVisible = value is not null;
        }
    }

    private void Slider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateValueText();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Label_DoubleTapped(object? sender, TappedEventArgs e) => Value = DefaultValue;

    private void UpdateValueText()
    {
        double v = Math.Round(Slider.Value, _decimals);
        string sign = ShowSign && v > 0 ? "+" : "";
        ValueText.Text = sign + v.ToString("F" + _decimals, CultureInfo.CurrentCulture) + Unit;
    }
}
