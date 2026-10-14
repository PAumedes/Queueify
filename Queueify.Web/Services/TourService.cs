namespace Queueify.Web.Services;

/// <summary>
/// Lets any page ask for the quick tour to run again. The tour component mounted in the layout (or, failing
/// that, on the Help page) listens; only one mounted component shows it, so two never overlap.
/// </summary>
public sealed class TourService
{
    private object? _owner;

    public event Action? Replayed;

    public void Replay() => Replayed?.Invoke();

    /// <summary>True for the first component to attach until it detaches.</summary>
    public bool TryAttach(object component)
    {
        lock (this) return _owner is null && (_owner = component) is not null;
    }

    public void Detach(object component)
    {
        lock (this) if (ReferenceEquals(_owner, component)) _owner = null;
    }
}
