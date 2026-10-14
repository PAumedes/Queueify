using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Queueify.Launcher;

/// <summary>
/// The download's entry point: unpacks the app appended to this exe (once per version) into
/// %LOCALAPPDATA%\Queueify\app and starts it, passing its own path so the app finds a portable Data
/// folder next to it and pins itself to the taskbar through it.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        string cache;
        BundleInfo info;
        try
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("No path for this exe.");
            cache = Path.Combine(LocalAppData(), "Queueify", "app");
            string folder;
            using (var exe = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                info = Bundle.Read(exe) ?? throw new BundleDamagedException("No app appended.");
                folder = Bundle.Unpack(exe, info, cache);
            }

            // The app's own folder as the working one, not the download's, which others may be able to write to.
            var start = new ProcessStartInfo(Path.Combine(folder, "Queueify.exe")) { UseShellExecute = false, WorkingDirectory = folder };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["QUEUEIFY_EXE"] = self;
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox(0, StarterMessage.For(ex, (GetUserDefaultUILanguage() & 0x3FF) == 0x0A), "Queueify", 0x10 /* MB_ICONERROR */);
            return 1;
        }

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

    /// <summary>Same rule as the app (AppData.LocalAppDataRoot): the variable only when it's a full path.</summary>
    private static string LocalAppData() =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } v && v.IndexOfAny(['"', '<', '>', '*', '?', '|', '\0', '\t', '\r', '\n']) < 0 && Path.IsPathFullyQualified(v)
            ? v : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // The starter has no culture data (InvariantGlobalization keeps it small), so it asks Windows directly.
    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
