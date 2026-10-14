using System.Text.Json;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Where the queue, the settings and the layout live: %LOCALAPPDATA%\Queueify, or a "Data" folder next
/// to the exe when there is one (portable mode: 1.1.0 and earlier, or a copy on a USB stick).
/// </summary>
public static class AppData
{
    public static string Folder { get; } = FolderFor(
        StartedExe(Environment.GetEnvironmentVariable("QUEUEIFY_EXE"), Environment.ProcessPath),
        LocalAppDataRoot(Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        EntriesOf);

    private static string[]? EntriesOf(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFileSystemEntries(dir).Select(e => Path.GetFileName(e)).ToArray() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // unreadable: not a portable copy we can use
        }
    }

    /// <summary>
    /// The exe the user opened: the download's starter (QUEUEIFY_EXE, set when it runs the app from its
    /// unpacked copy) when there is one, so a Data folder next to it still makes the app portable.
    /// </summary>
    public static string? StartedExe(string? starter, string? processPath) =>
        starter is { Length: > 0 } s && s.IndexOfAny(NeverInAPath) < 0 && Path.IsPathFullyQualified(s) ? s : processPath;

    /// <summary>What a taskbar pin runs: the exe the user opened, quoted (its path may have spaces).</summary>
    public static string RelaunchCommand(string startedExe) => $"\"{startedExe}\"";

    /// <summary>
    /// The LOCALAPPDATA variable when it's a full path (package.ps1's smoke test points it at a scratch
    /// folder), otherwise Windows' known folder: a relative or quoted value would scatter the data.
    /// </summary>
    public static string LocalAppDataRoot(string? variable, string knownFolder) =>
        variable is { Length: > 0 } v && v.IndexOfAny(NeverInAPath) < 0 && Path.IsPathFullyQualified(v) ? v : knownFolder;

    // Path.GetInvalidPathChars() leaves out " < > * ?, which Windows refuses in any folder name.
    private static ReadOnlySpan<char> NeverInAPath => "\"<>*?|\0\t\r\n";

    private static bool IsQueueifyFile(string name) => name.ToLowerInvariant() is "queueify.db" or "settings.json" or "ui.json";

    public static string DatabasePath => Path.Combine(Folder, "queueify.db");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    /// <summary>
    /// A "Data" folder next to the exe the user started is used when it's Queueify's (it holds Queueify's
    /// files, or is empty to start a portable copy); someone's own Documents\Data is left alone. Next to
    /// the exe, not <see cref="AppContext.BaseDirectory"/>: the single-file app runs from a copy unpacked
    /// into a temporary folder. <paramref name="dataFolderEntries"/> lists a folder's entries, or null
    /// when it isn't a folder.
    /// </summary>
    public static string FolderFor(string? exePath, string localAppData, Func<string, string[]?> dataFolderEntries) =>
        Path.GetDirectoryName(exePath) is { Length: > 0 } dir
        && dataFolderEntries(Path.Combine(dir, "Data")) is { } entries
        && (entries.Length == 0 || entries.Any(IsQueueifyFile))
            ? Path.Combine(dir, "Data")
            : Path.Combine(localAppData, "Queueify");

    public static AppSettings LoadSettings(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.AppSettings) is { } settings)
            {
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged settings file shouldn't stop the app; the user can re-enter keys.
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings, string? path = null)
    {
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, StorageJson.Default.AppSettings));
    }
}
