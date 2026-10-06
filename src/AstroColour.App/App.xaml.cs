using Microsoft.UI.Xaml;

namespace AstroColour.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    /// <summary>%LOCALAPPDATA%\AstroColourEditor, for the crash log and saved window placement.</summary>
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AstroColourEditor");

    /// <summary>Path of the crash log, in <see cref="DataFolder"/>.</summary>
    public static string CrashLogPath { get; } = Path.Combine(DataFolder, "crash.log");

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch
        {
            // Nothing more can be done if the log cannot be written.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // Support "Open with" / drag-onto-exe: the first argument is a file to open.
        var commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1 && File.Exists(commandLine[1]))
            _window.OpenOnStartup(commandLine[1]);
    }
}
