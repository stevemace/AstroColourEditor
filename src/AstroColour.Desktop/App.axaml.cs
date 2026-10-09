using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AstroColour.Desktop;

public partial class App : Application
{
    /// <summary>
    /// Folder for the crash log and saved window placement: %LOCALAPPDATA%\AstroColourEditor on Windows,
    /// ~/Library/Application Support/AstroColourEditor on macOS.
    /// </summary>
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstroColourEditor");

    /// <summary>Path of the crash log, in <see cref="DataFolder"/>.</summary>
    public static string CrashLogPath { get; } = Path.Combine(DataFolder, "crash.log");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        Dispatcher.UIThread.UnhandledException += (_, e) => LogCrash(e.Exception);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            // Windows "Open with" / drag-onto-exe: the first argument is a file to open.
            if (desktop.Args is [var first, ..] && File.Exists(first))
                window.Opened += (_, _) => window.OpenFromShell(first);

            // macOS "Open with" / dropping a file on the Dock icon arrives as an activation instead.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs { Files: [var file, ..] } && file.TryGetLocalPath() is { } path)
                        window.OpenFromShell(path);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Nothing more can be done if the log cannot be written.
        }
    }
}
