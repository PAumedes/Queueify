namespace Queueify.Services;

public sealed record Shortcut(string Keys, string Description);

/// <summary>Keyboard shortcuts, listed once for the Help page.</summary>
public static class Shortcuts
{
    public static IReadOnlyList<Shortcut> All { get; } =
    [
        new("Enter", "In the search box: search, then press again to add the highlighted album"),
        new("↑ ↓", "Choose another search result"),
        new("Esc", "Clear the search box"),
        new("Ctrl+K", "Jump to the search box (Ctrl+N works too)"),
        new("Ctrl+F", "Filter your queue"),
        new("Ctrl+B", "Hide or show the Add albums panel"),
        new("Ctrl+V", "Load a magazine photo from the clipboard"),
        new("F1", "Open Help"),
    ];
}
