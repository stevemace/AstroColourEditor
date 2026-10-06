using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AstroColour.App;

/// <summary>
/// The main window's restored bounds (physical pixels) and maximized state, kept between runs
/// in %LOCALAPPDATA%\AstroColourEditor\window.json.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    private static readonly string FilePath = Path.Combine(App.DataFolder, "window.json");

    [JsonIgnore]
    public RectInt32 Bounds => new(X, Y, Width, Height);

    /// <summary>The saved placement, or null if there is none or it no longer fits on any display.</summary>
    public static WindowPlacement? Load()
    {
        try
        {
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
            if (placement is null || placement.Width < 200 || placement.Height < 150) return null;

            // Require the title bar to be on a display, so the window can always be dragged back.
            var titleBar = new RectInt32(placement.X, placement.Y, placement.Width, 40);
            return DisplayArea.GetFromRect(titleBar, DisplayAreaFallback.None) is null ? null : placement;
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
