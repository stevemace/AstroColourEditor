using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AstroColour.Desktop.Controls;

public enum DialogResult
{
    None,
    Primary,
    Secondary,
}

/// <summary>A modal message box with up to three buttons, like WinUI's ContentDialog.</summary>
internal sealed class MessageDialog : Window
{
    private MessageDialog(string title, string message, string? primary, string? secondary, string close, DialogResult defaultButton)
    {
        // The heading is in the content, so the title bar only names the app (macOS dialogs have none).
        Title = OperatingSystem.IsMacOS() ? "" : "Astro Colour Editor";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttons = new List<Button>();
        if (primary is not null) buttons.Add(MakeButton(primary, DialogResult.Primary));
        if (secondary is not null) buttons.Add(MakeButton(secondary, DialogResult.Secondary));
        var closeButton = MakeButton(close, DialogResult.None);
        closeButton.IsCancel = true;
        buttons.Add(closeButton);
        // macOS puts the default action last (rightmost), with Cancel next to it.
        if (OperatingSystem.IsMacOS()) buttons.Reverse();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonRow.Children.AddRange(buttons);

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Width = 420,
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttonRow,
            },
        };

        Button MakeButton(string text, DialogResult result)
        {
            var button = new Button { Content = text, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (result == defaultButton && result != DialogResult.None)
            {
                button.IsDefault = true;
                button.Classes.Add("accent");
            }
            button.Click += (_, _) => Close(result);
            return button;
        }
    }

    public static Task<DialogResult> ShowAsync(Window owner, string title, string message,
        string? primary = null, string? secondary = null, string close = "OK", DialogResult defaultButton = DialogResult.None) =>
        new MessageDialog(title, message, primary, secondary, close, defaultButton).ShowDialog<DialogResult>(owner);
}
