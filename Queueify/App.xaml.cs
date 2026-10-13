using Microsoft.UI.Xaml;
using Queueify.Services;

namespace Queueify;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        // Errors in fire-and-forget background work would otherwise vanish silently.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            throw;
        }
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(AppData.Folder);
            File.AppendAllText(Path.Combine(AppData.Folder, "crash.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException)
        {
            // Nothing sensible left to do if the log itself can't be written.
        }
    }
}
