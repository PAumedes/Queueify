using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Queueify.Launcher;

/// <summary>
/// The download's entry point: unpacks the app appended to this exe (once per version) into
/// %LOCALAPPDATA%\Queueify\app and starts it, passing its own path so the app finds a portable Data
/// folder next to it and pins itself to the taskbar through it. A loading screen covers the wait.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        var spanish = (GetUserDefaultUILanguage() & 0x3FF) == 0x0A;
        string cache;
        BundleInfo info;
        LoadingScreen? loading = null;
        try
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("No path for this exe.");
            cache = Path.Combine(LocalAppData(), "Queueify", "app");
            string folder;
            using (var exe = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                info = Bundle.Read(exe) ?? throw new BundleDamagedException("No app appended.");
                var unpacking = !Bundle.IsUnpacked(cache, info);
                loading = LoadingScreen.Show(StarterMessage.Loading(unpacking, spanish), StarterMessage.LoadingDelay(unpacking));
                folder = Bundle.Unpack(exe, info, cache);
            }

            // The app's own folder as the working one, not the download's, which others may be able to write to.
            var start = new ProcessStartInfo(Path.Combine(folder, "Queueify.exe")) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["QUEUEIFY_EXE"] = self;
            using var app = Process.Start(start);
            // The loading screen has the foreground; the app's window takes it over when it opens.
            if (app != null) AllowSetForegroundWindow(app.Id);
            // Gone before its window opened: say so, rather than leave the user with nothing on screen.
            if (WaitForWindow(app) is { } exitCode && exitCode != 0)
            {
                loading.Dispose();
                MessageBox(0, StarterMessage.ClosedWhileStarting(exitCode, spanish), "Queueify", MB_ICONWARNING | OnTop);
                return 1;
            }
        }
        catch (Exception ex)
        {
            loading?.Dispose();
            MessageBox(0, StarterMessage.For(ex, spanish), "Queueify", MB_ICONERROR | OnTop);
            return 1;
        }
        loading.Dispose();

        try
        {
            Bundle.RemoveOtherVersions(cache, info.Id);
        }
        catch (Exception)
        {
            // Tidying up is for next time; the app is already running.
        }
        return 0;
    }

    /// <summary>Until the app's window is open, so the loading screen hands over to it; the exit code if it ended first.</summary>
    private static int? WaitForWindow(Process? app)
    {
        if (app == null) return null;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            app.Refresh();
            if (app.HasExited) return app.ExitCode;
            if (app.MainWindowHandle != 0) return null;
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>Same rule as the app (AppData.LocalAppDataRoot): the variable only when it's a full path.</summary>
    private static string LocalAppData() =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } v && v.IndexOfAny(['"', '<', '>', '*', '?', '|', '\0', '\t', '\r', '\n']) < 0 && Path.IsPathFullyQualified(v)
            ? v : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private const uint MB_ICONERROR = 0x10, MB_ICONWARNING = 0x30;
    /// <summary>MB_SETFOREGROUND | MB_TOPMOST: the loading screen that had the foreground is gone, and the message
    /// mustn't open behind other windows (or, after an update, behind the old Queueify waiting for it).</summary>
    private const uint OnTop = 0x10000 | 0x40000;

    // The starter has no culture data (InvariantGlobalization keeps it small), so it asks Windows directly.
    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
