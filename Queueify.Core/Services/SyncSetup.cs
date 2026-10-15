using Queueify.Models;

namespace Queueify.Services;

/// <summary>Where the sync setup dialog connects this PC: a Queueify web server, or a Firestore queue (created or joined).</summary>
public sealed record SyncTarget(SyncKind Kind, string Where, string Secret, bool Joining)
{
    public static SyncTarget Web(string serverUrl, string token) => new(SyncKind.Web, serverUrl, token, true);

    public static SyncTarget JoinCloud(string projectId, string queueKey) => new(SyncKind.Cloud, projectId, queueKey, true);

    public static SyncTarget CreateCloud(string projectId, string queueKey) => new(SyncKind.Cloud, projectId, queueKey, false);
}

/// <summary>Connecting a PC to an online queue: tried on a copy of the settings, so a failure leaves nothing half-connected.</summary>
public static class SyncSetup
{
    /// <summary>
    /// Connects a copy of <paramref name="current"/> to <paramref name="target"/> and runs the first sync with it. Returns
    /// the connected settings for the caller to keep; throws whatever the sync throws, and then <paramref name="current"/>
    /// has not been touched. <paramref name="previousSync"/>, if one is still finishing, ends first: its "uploaded" marks
    /// would otherwise undo the "everything is dirty" below.
    /// </summary>
    public static async Task<AppSettings> TryConnectAsync(
        BacklogStore store, AppSettings current, SyncTarget target, ISyncProvider cloud, ISyncProvider web,
        Task? previousSync, CancellationToken ct)
    {
        var trial = current.Clone();
        if (target.Kind == SyncKind.Web) trial.ConnectWeb(target.Where, target.Secret);
        else trial.ConnectCloud(target.Where, target.Secret);

        // Creating: this PC's keys go up. Joining: the online queue's keys win (or this PC's, if the queue has none yet).
        trial.SharedSettingsUpdatedAt = target.Joining ? null
            : trial.SpotifyConfigured || trial.TidalConfigured ? BacklogStore.Now() : null;

        if (previousSync != null)
        {
            try { await previousSync; }
            catch (Exception) { /* that sync reported its own failure when it ran; it only matters that it has ended */ }
        }
        // Albums synced to an earlier queue are marked clean; this queue needs all of them.
        store.MarkAllDirty();
        await Task.Run(() => SyncProviders.For(trial.ActiveSync, cloud, web).SyncAsync(store, trial, ct), ct);
        return trial;
    }
}
