namespace Queueify.Web.Ocr;

/// <summary>
/// Admission for reading photos: one at a time per user, and a cap across the server. A reader costs
/// seconds of CPU, so a turned-away request is cheaper than a queue that grows without bound.
/// </summary>
public sealed class OcrGate(OcrOptions options)
{
    private readonly HashSet<string> _users = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>A slot to dispose when the photo is done, or null when this user (or the server) is busy.</summary>
    public IDisposable? TryEnter(string userId)
    {
        lock (_lock)
        {
            if (_users.Count >= options.MaxConcurrent || !_users.Add(userId)) return null;
        }
        return new Slot(this, userId);
    }

    private sealed class Slot(OcrGate gate, string userId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (gate._lock) gate._users.Remove(userId);
        }
    }
}
