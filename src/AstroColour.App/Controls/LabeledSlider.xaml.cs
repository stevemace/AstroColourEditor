using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AstroColour.App.Controls;

/// <summary>A slider with a label, a live value readout, an optional colour ramp and double-click reset.</summary>
public sealed partial class LabeledSlider : UserControl
{
    public LabeledSlider()
    {
        InitializeComponent();
        UpdateValueText();
    }

    public event EventHandler? ValueChanged;

    public string Label
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

    public string Unit { get; set; } = "";

    /// <summary>Show a leading "+" for positive values (for relative adjustments).</summary>
    public bool ShowSign { get; set; }

    public Brush? GradientBrush
    {
        get => Gradient.Fill;
        set
        {
            Gradient.Fill = value;
            Gradient.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateValueText();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Label_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Value = DefaultValue;

    private void UpdateValueText()
    {
        double v = Math.Round(Slider.Value);
        string sign = ShowSign && v > 0 ? "+" : "";
        ValueText.Text = sign + v.ToString(CultureInfo.CurrentCulture) + Unit;
    }
}
