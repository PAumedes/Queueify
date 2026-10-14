using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

public static class QueueifyStorageExtensions
{
    /// <summary>
    /// The data folder: <c>Queueify:DataRoot</c>, or "App_Data" beside the app ("Data" already holds the account
    /// database). Holds one folder per user (their ids are GUIDs) plus "keys" and "tessdata".
    /// </summary>
    public static string DataRootOf(IConfiguration config, IHostEnvironment env) =>
        Path.GetFullPath(config["Queueify:DataRoot"] is { Length: > 0 } root ? root : Path.Combine(env.ContentRootPath, "App_Data"));

    /// <summary>
    /// The per-user stores, with the Spotify/Tidal secrets encrypted by Data Protection. Its key ring lives
    /// in the data folder, so it travels with the backups and survives restarts.
    /// </summary>
    public static IServiceCollection AddQueueifyStorage(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var root = DataRootOf(config, env);
        services.AddDataProtection().SetApplicationName("Queueify").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
        services.TryAddSingleton(sp =>
            new UserStores(root, sp.GetRequiredService<IDataProtectionProvider>().CreateProtector("Queueify.UserSecrets.v1")));
        return services;
    }
}
