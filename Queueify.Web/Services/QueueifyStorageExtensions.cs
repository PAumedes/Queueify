namespace Queueify.Web.Services;

public static class QueueifyStorageExtensions
{
    /// <summary>
    /// The data folder: <c>Queueify:DataDir</c>, or "App_Data" beside the app. Holds one folder per user (their ids are
    /// GUIDs) plus "keys", "tessdata", the account database and the owner marker. The per-user stores and the Data
    /// Protection key ring are registered by <c>AddQueueifySync</c>. Backing up this one folder backs up everything.
    /// </summary>
    public static string DataRootOf(IConfiguration config, IHostEnvironment env) =>
        Path.GetFullPath(config["Queueify:DataDir"] is { Length: > 0 } root ? root : Path.Combine(env.ContentRootPath, "App_Data"));

    /// <summary>
    /// The account database: <c>ConnectionStrings:DefaultConnection</c> when an administrator sets one, otherwise
    /// <c>identity.db</c> in the data folder, created on first run (nothing ships with the app).
    /// </summary>
    public static string AccountConnectionString(IConfiguration config, IHostEnvironment env)
    {
        if (config.GetConnectionString("DefaultConnection") is { Length: > 0 } configured) return configured;
        var root = Directory.CreateDirectory(DataRootOf(config, env)).FullName;
        return $"DataSource={Path.Combine(root, "identity.db")}";
    }
}
