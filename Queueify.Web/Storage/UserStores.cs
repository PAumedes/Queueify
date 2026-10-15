using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Web.Storage;

/// <summary>
/// One folder per user under the data root, holding their queue (queueify.db) and settings.json.
/// With a <paramref name="protector"/> the Spotify and Tidal secrets are kept encrypted in secrets.dat,
/// bound to the user, and never written to settings.json.
/// </summary>
public sealed class UserStores(string root, IDataProtector? protector = null)
{
    private string FolderOf(string userId)
    {
        // An allowlist: "." or a name the server keeps for itself must never become a user folder.
        if (userId.Length is 0 or > 64 || userId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || userId.Equals("keys", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Not a valid user id.", nameof(userId));
        return Path.Combine(root, userId);
    }

    /// <summary>A file in the user's folder, by a plain name (no folders).</summary>
    public string FileOf(string userId, string name)
    {
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Contains(".."))
            throw new ArgumentException("Not a valid file name.", nameof(name));
        return Path.Combine(FolderOf(userId), name);
    }

    public string QueuePathOf(string userId) => Path.Combine(FolderOf(userId), "queueify.db");

    public BacklogStore QueueOf(string userId) => new(QueuePathOf(userId));

    public AppSettings LoadSettings(string userId)
    {
        var folder = FolderOf(userId);
        var settings = AppData.LoadSettings(Path.Combine(folder, "settings.json"));
        var secretsPath = Path.Combine(folder, "secrets.dat");
        if (protector != null && File.Exists(secretsPath))
        {
            try
            {
                var secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    protector.CreateProtector(userId).Unprotect(File.ReadAllText(secretsPath))) ?? [];
                settings.SpotifyClientSecret = secrets.GetValueOrDefault(nameof(AppSettings.SpotifyClientSecret), "");
                settings.TidalClientSecret = secrets.GetValueOrDefault(nameof(AppSettings.TidalClientSecret), "");
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException)
            {
                // Lost keys or a damaged file: the user enters the secrets again.
                settings.SpotifyClientSecret = settings.TidalClientSecret = "";
            }
        }
        return settings;
    }

    public void SaveSettings(string userId, AppSettings settings)
    {
        var folder = FolderOf(userId);
        if (protector == null)
        {
            AppData.SaveSettings(settings, Path.Combine(folder, "settings.json"));
            return;
        }

        Directory.CreateDirectory(folder);
        var secrets = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [nameof(AppSettings.SpotifyClientSecret)] = settings.SpotifyClientSecret,
            [nameof(AppSettings.TidalClientSecret)] = settings.TidalClientSecret,
        });
        WriteAtomically(Path.Combine(folder, "secrets.dat"), protector.CreateProtector(userId).Protect(secrets));

        var plain = settings.Clone();
        plain.SpotifyClientSecret = plain.TidalClientSecret = "";
        AppData.SaveSettings(plain, Path.Combine(folder, "settings.json"));
    }

    /// <summary>Removes everything stored for the user: queue, settings, secrets, preferences.</summary>
    public void Delete(string userId)
    {
        var folder = FolderOf(userId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
