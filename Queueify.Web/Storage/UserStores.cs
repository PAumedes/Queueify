using Queueify.Models;
using Queueify.Services;

namespace Queueify.Web.Storage;

/// <summary>One folder per user under the data root, holding their queue (queueify.db) and settings.json.</summary>
public sealed class UserStores(string root)
{
    private string FolderOf(string userId)
    {
        if (userId.Length == 0 || userId.IndexOfAny(['/', '\\', ':']) >= 0 || userId.Contains(".."))
            throw new ArgumentException("Not a valid user id.", nameof(userId));
        return Path.Combine(root, userId);
    }

    public BacklogStore QueueOf(string userId) => new(Path.Combine(FolderOf(userId), "queueify.db"));

    public AppSettings LoadSettings(string userId) => AppData.LoadSettings(Path.Combine(FolderOf(userId), "settings.json"));

    public void SaveSettings(string userId, AppSettings settings) =>
        AppData.SaveSettings(settings, Path.Combine(FolderOf(userId), "settings.json"));
}
