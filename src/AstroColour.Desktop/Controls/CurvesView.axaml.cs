using AstroColour.Core.Curves;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AstroColour.Desktop.Controls;

/// <summary>Channel buttons, a <see cref="CurveEditor"/> and reset links for editing a <see cref="CurveSet"/>.</summary>
public partial class CurvesView : UserControl
{
    private const double WarnFraction = 0.005; // clipping shown in amber from this share of pixels

    private CurveSet? _curves;
    private CurveClipping? _clipping;
    private float[][]? _histogram;
    private CurveChannel _channel = CurveChannel.Rgb;

    public CurvesView()
    {
        InitializeComponent();
        ShowChannel(CurveChannel.Rgb);
    }

    /// <summary>Raised when the curves change; the argument is true while a point is still being dragged.</summary>
    public event EventHandler<bool>? CurvesChanged;

    /// <summary>The curves being edited, which this control changes in place.</summary>
    public CurveSet? Curves
    {
        get => _curves;
        set
        {
            _curves = value;
            ShowChannel(_channel);
        }
    }

    /// <summary>Histograms indexed by <see cref="CurveChannel"/>, as from <see cref="CurveSet.Histogram"/>.</summary>
    public float[][]? Histogram
    {
        get => _histogram;
        set
        {
            _histogram = value;
            Graph.Histogram = value?[(int)_channel];
        }
    }

    /// <summary>A sample of the image for showing how many pixels the curves clip; nothing is shown if null.</summary>
    public CurveClipping? Clipping
    {
        get => _clipping;
        set
        {
            _clipping = value;
            UpdateClipping();
        }
    }

    /// <summary>Allow the per-channel curves; a single-channel image only has the composite curve.</summary>
    public bool ChannelsEnabled
    {
        get => RedButton.IsEnabled;
        set
        {
            RedButton.IsEnabled = GreenButton.IsEnabled = BlueButton.IsEnabled = value;
            if (!value) ShowChannel(CurveChannel.Rgb);
            UpdateClipping();
        }
    }

    public string? Hint
    {
        get => HintText.Text;
        set => HintText.Text = value;
    }

    public void ShowChannel(CurveChannel channel)
    {
        _channel = channel;
        RgbButton.IsChecked = channel == CurveChannel.Rgb;
        RedButton.IsChecked = channel == CurveChannel.Red;
        GreenButton.IsChecked = channel == CurveChannel.Green;
        BlueButton.IsChecked = channel == CurveChannel.Blue;

        Graph.Curve = _curves?[channel];
        Graph.Histogram = _histogram?[(int)channel];
        Graph.CurveColour = channel switch
        {
            CurveChannel.Red => Color.FromRgb(0xE8, 0x4A, 0x4F),
            CurveChannel.Green => Color.FromRgb(0x3F, 0xB9, 0x50),
            CurveChannel.Blue => Color.FromRgb(0x4C, 0x8D, 0xFF),
            _ => null,
        };
        ChannelText.Text = channel == CurveChannel.Rgb ? "RGB curve" : $"{channel} curve";
        UpdateClipping();
    }

    private void UpdateClipping()
    {
        ClippingText.IsVisible = _clipping is not null && _curves is not null;
        if (_clipping is null || _curves is null) return;

        var (shadows, highlights) = _clipping.Measure(_curves, _channel, monochrome: !ChannelsEnabled);
        ClippingText.Inlines = [
            new Run("Clipped:  shadows "),
            Amount(shadows),
            new Run("   highlights "),
            Amount(highlights),
        ];
    }

    private static Run Amount(double fraction)
    {
        var run = new Run(fraction <= 0 ? "0%" : fraction < 0.0001 ? "<0.01%" : $"{fraction * 100:0.##}%")
        {
            FontWeight = FontWeight.SemiBold,
        };
        if (fraction >= WarnFraction) run.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x2E));
        return run;
    }

    private void Channel_Click(object? sender, RoutedEventArgs e) =>
        ShowChannel(sender == RedButton ? CurveChannel.Red
            : sender == GreenButton ? CurveChannel.Green
            : sender == BlueButton ? CurveChannel.Blue
            : CurveChannel.Rgb);

    private void Graph_CurveChanged(object? sender, bool interactive)
    {
        UpdateClipping();
        CurvesChanged?.Invoke(this, interactive);
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (_curves is null || _curves[_channel].IsIdentity) return;
        _curves[_channel].Reset();
        Graph.InvalidateVisual();
        UpdateClipping();
        CurvesChanged?.Invoke(this, false);
    }

    private void ResetAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_curves is null || _curves.IsIdentity) return;
        _curves.Reset();
        Graph.InvalidateVisual();
        UpdateClipping();
        CurvesChanged?.Invoke(this, false);
    }
}
