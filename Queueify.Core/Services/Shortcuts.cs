namespace Queueify.Services;

public sealed record Shortcut(string Keys, string Description);

/// <summary>Keyboard shortcuts, listed once for the Help page.</summary>
public static class Shortcuts
{
    public static IReadOnlyList<Shortcut> All =>
    [
        new("Enter", Loc.T("keys.enter")),
        new("↑ ↓", Loc.T("keys.arrows")),
        new("Esc", Loc.T("keys.esc")),
        new("Ctrl+K", Loc.T("keys.ctrl_k")),
        new("Ctrl+F", Loc.T("keys.ctrl_f")),
        new("Ctrl+B", Loc.T("keys.ctrl_b")),
        new("Ctrl+V", Loc.T("keys.ctrl_v")),
        new("F1", Loc.T("keys.f1")),
    ];

    /// <summary>The shortcuts the web app handles: no side panel to hide, and Ctrl+K opens the Add page.</summary>
    public static IReadOnlyList<Shortcut> Web =>
    [
        new("Enter", Loc.T("keys.enter")),
        new("↑ ↓", Loc.T("keys.arrows")),
        new("Esc", Loc.T("keys.esc")),
        new("Ctrl+K", Loc.T("web.keys.ctrl_k")),
        new("Ctrl+F", Loc.T("keys.ctrl_f")),
        new("Ctrl+V", Loc.T("keys.ctrl_v")),
        new("F1", Loc.T("keys.f1")),
    ];
}