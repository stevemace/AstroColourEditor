using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AstroColour.Desktop.Controls;

public enum InfoBarSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>A closable message banner, like WinUI's InfoBar.</summary>
public partial class InfoBar : UserControl
{
    public InfoBar()
    {
        InitializeComponent();
        Severity = InfoBarSeverity.Informational;
    }

    public string? Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string? Message
    {
        get => MessageText.Text;
        set
        {
            MessageText.Text = value;
            MessageText.IsVisible = !string.IsNullOrEmpty(value);
        }
    }

    public bool IsOpen
    {
        get => IsVisible;
        set => IsVisible = value;
    }

    public InfoBarSeverity Severity
    {
        get;
        set
        {
            field = value;
            var (background, accent) = value switch
            {
                InfoBarSeverity.Success => (Color.Parse("#E61C3324"), Color.Parse("#FF4ADE80")),
                InfoBarSeverity.Warning => (Color.Parse("#E6382C12"), Color.Parse("#FFFBBF24")),
                InfoBarSeverity.Error => (Color.Parse("#E63A1A1A"), Color.Parse("#FFF87171")),
                _ => (Color.Parse("#E61A2838"), Color.Parse("#FF60A5FA")),
            };
            Frame.Background = new SolidColorBrush(background);
            Frame.BorderBrush = new SolidColorBrush(accent, 0.45);
            Dot.Fill = new SolidColorBrush(accent);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => IsOpen = false;
}
