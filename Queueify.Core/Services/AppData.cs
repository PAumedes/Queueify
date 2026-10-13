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
        Environment.ProcessPath,
        // The variable first, so the UI tests can point a test run somewhere else.
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Directory.Exists);
    public static string DatabasePath => Path.Combine(Folder, "queueify.db");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    /// <summary>
    /// Next to the exe the user started, not <see cref="AppContext.BaseDirectory"/>: the single-file app
    /// runs from a copy unpacked into a temporary folder.
    /// </summary>
    public static string FolderFor(string? exePath, string localAppData, Func<string, bool> directoryExists) =>
        Path.GetDirectoryName(exePath) is { Length: > 0 } dir && directoryExists(Path.Combine(dir, "Data"))
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
