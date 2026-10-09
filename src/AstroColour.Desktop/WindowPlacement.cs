using System.Text.Json;
using Avalonia;
using Avalonia.Platform;

namespace AstroColour.Desktop;

/// <summary>
/// The main window's restored position (screen pixels), client size (DIPs) and maximized state,
/// kept between runs in <see cref="App.DataFolder"/>/placement.json.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized)
{
    private static readonly string FilePath = Path.Combine(App.DataFolder, "placement.json");

    /// <summary>The saved placement, or null if there is none or it no longer fits on any display.</summary>
    public static WindowPlacement? Load(IReadOnlyList<Screen> screens)
    {
        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
            if (placement is null || placement.Width < 200 || placement.Height < 150) return null;

            // Require the top of the window to be on a display, so it can always be dragged back.
            return screens.Any(s => s.WorkingArea.Intersects(
                new PixelRect(placement.X, placement.Y, (int)(placement.Width * s.Scaling), (int)(40 * s.Scaling))))
                ? placement
                : null;
        }
        catch
        {
            return null; // Missing or unreadable: use the default placement.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch
        {
            // Not worth bothering the user about.
        }
    }
}
