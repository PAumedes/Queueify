using Queueify.Models;

namespace Queueify.Services;

/// <summary>Which online queue this PC syncs with; only one is used at a time.</summary>
public enum SyncKind { None, Cloud, Web }

/// <summary>A way of keeping the queue in step with other PCs (the user's Firestore, or a Queueify web server).</summary>
public interface ISyncProvider
{
    Task<SyncResult> SyncAsync(BacklogStore store, AppSettings settings, CancellationToken ct);
}

public static class SyncProviders
{
    public static ISyncProvider For(SyncKind kind, CloudSync cloud, ServerSync web) => kind switch
    {
        SyncKind.Web => web,
        SyncKind.Cloud => cloud,
        _ => throw new InvalidOperationException("Sync isn't set up."),
    };
}
