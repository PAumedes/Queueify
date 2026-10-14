using Microsoft.AspNetCore.DataProtection;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Web.Storage;

/// <summary>
/// One folder per user under the data root, holding their queue (queueify.db) and settings.json. With a
/// <paramref name="protector"/> the two client secrets are encrypted in settings.json (ASP.NET Data
/// Protection); callers always see them in clear text, and nothing else is encrypted.
/// </summary>
public sealed class UserStores(string root, IDataProtector? protector = null)
{
    private const string Prefix = "dp1:";

    private string FolderOf(string userId)
    {
        if (userId.Length == 0 || userId.IndexOfAny(['/', '\\', ':']) >= 0 || userId.Contains(".."))
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

    public BacklogStore QueueOf(string userId) => new(Path.Combine(FolderOf(userId), "queueify.db"));

    public AppSettings LoadSettings(string userId)
    {
        var settings = AppData.LoadSettings(FileOf(userId, "settings.json"));
        settings.SpotifyClientSecret = Reveal(settings.SpotifyClientSecret);
        settings.TidalClientSecret = Reveal(settings.TidalClientSecret);
        return settings;
    }

    public void SaveSettings(string userId, AppSettings settings)
    {
        var stored = settings.Clone();
        stored.SpotifyClientSecret = Hide(stored.SpotifyClientSecret);
        stored.TidalClientSecret = Hide(stored.TidalClientSecret);
        AppData.SaveSettings(stored, FileOf(userId, "settings.json"));
    }

    /// <summary>Removes everything stored for the user: queue, settings, preferences.</summary>
    public void Delete(string userId)
    {
        var folder = FolderOf(userId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private string Hide(string secret) => protector is null || secret.Length == 0 ? secret : Prefix + protector.Protect(secret);

    private string Reveal(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        try
        {
            return protector?.Unprotect(stored[Prefix.Length..]) ?? "";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "";   // keys were lost or replaced: the user types the secret again
        }
    }
}
