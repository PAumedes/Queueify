namespace Queueify.Web.Services;

public static class QueueifyStorageExtensions
{
    /// <summary>
    /// The data folder: <c>Queueify:DataDir</c>, or "App_Data" beside the app ("Data" already holds the account
    /// database). Holds one folder per user (their ids are GUIDs) plus "keys" and "tessdata". The per-user stores and
    /// the Data Protection key ring are registered by <c>AddQueueifySync</c>.
    /// </summary>
    public static string DataRootOf(IConfiguration config, IHostEnvironment env) =>
        Path.GetFullPath(config["Queueify:DataDir"] is { Length: > 0 } root ? root : Path.Combine(env.ContentRootPath, "App_Data"));
}
