using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Queueify.Launcher;

/// <summary>
/// The download's entry point: unpacks the app appended to this exe (once per version) into
/// %LOCALAPPDATA%\Queueify\app and starts it, passing its own path so the app finds a portable Data
/// folder next to it and restarts the same way.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var self = Environment.ProcessPath ?? throw new InvalidOperationException("no exe path");
            var cache = Path.Combine(LocalAppData(), "Queueify", "app");
            BundleInfo info;
            string folder;
            using (var exe = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                info = Bundle.Read(exe) ?? throw new InvalidDataException(Text(
                    "This copy of Queueify is incomplete. Download it again.",
                    "Esta copia de Queueify está incompleta. Descargala de nuevo."));
                folder = Bundle.Unpack(exe, info, cache);
            }

            var start = new ProcessStartInfo(Path.Combine(folder, "Queueify.exe")) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(self) };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["QUEUEIFY_EXE"] = self;
            Process.Start(start)?.Dispose();

            Bundle.RemoveOtherVersions(cache, info.Id);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Win32Exception or InvalidOperationException)
        {
            MessageBox(0, Text($"Queueify couldn't start.\n\n{ex.Message}", $"Queueify no pudo arrancar.\n\n{ex.Message}"), "Queueify", 0x10 /* MB_ICONERROR */);
            return 1;
        }
    }

    /// <summary>Same rule as the app (AppData.LocalAppDataRoot): the variable only when it's a full path.</summary>
    private static string LocalAppData() =>
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } v && v.IndexOfAny(['"', '<', '>', '*', '?', '|']) < 0 && Path.IsPathFullyQualified(v)
            ? v : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // The starter has no culture data (InvariantGlobalization keeps it small), so it asks Windows directly.
    private static string Text(string english, string spanish) => (GetUserDefaultUILanguage() & 0x3FF) == 0x0A ? spanish : english;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
