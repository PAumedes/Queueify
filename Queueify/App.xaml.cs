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
            // Before any window exists: XAML reads its texts while it loads.
            Loc.Use(UiState.Load().Language);
            // WinUI's own control texts ("Open Navigation", context menus) come from Windows' resources.
            try
            {
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Loc.Culture.Name;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // Only WinUI's built-in texts stay in Windows' language; not worth failing to start.
            }
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
