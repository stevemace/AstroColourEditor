using System.Text.Json;

namespace AstroColour.Desktop;

/// <summary>
/// Preferences kept between runs in <see cref="App.DataFolder"/>/settings.json: the recently opened files, most
/// recent first, and the folder last opened from or saved to.
/// </summary>
internal sealed class AppSettings
{
    public const int MaxRecentFiles = 10;

    private static readonly string FilePath = Path.Combine(App.DataFolder, "settings.json");

    public List<string> RecentFiles { get; set; } = [];

    public string? LastFolder { get; set; }

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            settings.RecentFiles ??= [];
            return settings;
        }
        catch
        {
            return new AppSettings(); // Missing or unreadable: start afresh.
        }
    }

    /// <summary>Puts <paramref name="path"/> at the top of the recent files and remembers its folder.</summary>
    public void AddRecent(string path)
    {
        path = Path.GetFullPath(path);
        RemoveRecent(path);
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        RememberFolder(path);
    }

    public void RemoveRecent(string path) => RecentFiles.RemoveAll(p => string.Equals(p, path, PathComparison));

    /// <summary>Remembers the folder of <paramref name="file"/> as the place to start the next file dialog.</summary>
    public void RememberFolder(string file)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } folder) LastFolder = folder;
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
