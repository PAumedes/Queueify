namespace Queueify.Web.Services;

/// <summary>One note at the bottom with an optional Undo (and a rating, right after "listened"), like the desktop app's InfoBar.</summary>
public sealed record Toast(string Message, string? ActionText = null, Action? Action = null, Action<double>? Rate = null, bool Warning = false);

/// <summary>The current note for one circuit; a newer note replaces an older one.</summary>
public sealed class Toasts
{
    public Toast? Current { get; private set; }

    public event Action? Changed;

    public void Show(Toast toast)
    {
        Current = toast;
        Changed?.Invoke();
    }

    public void Dismiss()
    {
        if (Current == null) return;
        Current = null;
        Changed?.Invoke();
    }
}
