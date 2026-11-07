using System.ComponentModel;
using AstroColour.Core.Colour;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AstroColour.Desktop;

/// <summary>List entry wrapping one <see cref="ColourAdjustment"/>.</summary>
public sealed class AdjustmentItem : INotifyPropertyChanged
{
    public AdjustmentItem(ColourAdjustment model)
    {
        Model = model;
    }

    public ColourAdjustment Model { get; }

    /// <summary>Whether the colour is being edited with curves rather than the HSL sliders.</summary>
    public bool EditWithCurves { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the user toggles the entry on or off.</summary>
    public event EventHandler? ActiveChanged;

    public string Name => $"{ColourMath.HueName(Model.TargetHue)}  {Math.Round(Model.TargetHue) % 360}°";

    public string Summary =>
        (Model.HasHslEdits, Model.Curves.IsIdentity) switch
        {
            (true, true) => Hsl,
            (true, false) => Hsl + "   Curves",
            (false, false) => "Curves",
            _ => "No adjustments",
        };

    private string Hsl => $"H {Signed(Model.HueShift)}°   S {Signed(Model.Saturation)}   L {Signed(Model.Lightness)}";

    /// <summary>The picked colour, re-hued to the current target hue.</summary>
    public IBrush Swatch
    {
        get
        {
            ColourMath.RgbToHsl(Model.SampleR, Model.SampleG, Model.SampleB, out _, out float s, out float l, out _);
            // Keep the swatch legible even for very dark or washed-out picks.
            ColourMath.HslToRgb(Model.TargetHue, Math.Max(s, 0.35f), Math.Clamp(l, 0.25f, 0.75f), out float r, out float g, out float b);
            return new ImmutableSolidColorBrush(Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
        }
    }

    public bool IsActive
    {
        get => Model.Enabled;
        set
        {
            if (Model.Enabled == value) return;
            Model.Enabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Swatch)));
    }

    private static string Signed(float v) => v > 0 ? $"+{v:0}" : $"{v:0}";
}
