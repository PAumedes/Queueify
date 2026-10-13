using System.Text.Json;
using Queueify.Models;

namespace Queueify.Services;

/// <summary>
/// Portable storage: everything lives in a "Data" folder next to Queueify.exe, so copying the app
/// folder to another PC copies the queue, the keys and the online sync connection too.
/// </summary>
public static class AppData
{
    public static string Folder { get; } = FolderFor(Environment.ProcessPath);
    public static string DatabasePath => Path.Combine(Folder, "queueify.db");
    private static string SettingsPath => Path.Combine(Folder, "settings.json");

    /// <summary>
    /// The folder next to the exe the user started. Not <see cref="AppContext.BaseDirectory"/>: the
    /// single-file app runs from a copy unpacked into a temporary folder.
    /// </summary>
    public static string FolderFor(string? exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath) is { Length: > 0 } dir ? dir : AppContext.BaseDirectory, "Data");

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
