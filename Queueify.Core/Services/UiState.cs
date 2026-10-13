using System.Text.Json;

namespace Queueify.Services;

/// <summary>
/// Per-PC layout preferences (panel width, view mode, window position, quick tour). Never synced:
/// each PC has its own screen.
/// </summary>
public sealed class UiState
{
    public static string DefaultPath => Path.Combine(AppData.Folder, "ui.json");

    public double PaneWidth { get; set; } = 420;
    public bool PaneVisible { get; set; } = true;
    public bool CoverView { get; set; }
    public string Sort { get; set; } = "oldest";

    /// <summary>Restored window bounds in physical pixels: x, y, width, height.</summary>
    public int[]? WindowBounds { get; set; }
    public bool Maximized { get; set; }

    /// <summary>The quick tour runs once on the first start; Help can replay it.</summary>
    public bool TourSeen { get; set; }

    /// <summary>"system" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>"system" (Windows' language), "en" or "es". Applied at startup.</summary>
    public string Language { get; set; } = "system";

    /// <summary>Which installed text recognition language reads photos: "auto" (Spanish when installed) or a tag.</summary>
    public string OcrLanguage { get; set; } = "auto";

    /// <summary>"Don't show this again" on the Photo page's offer to install Spanish text recognition.</summary>
    public bool OcrHintDismissed { get; set; }

    /// <summary>Back to the original layout (panel, list view, order, window size); the tour isn't repeated.</summary>
    public void ResetLayout()
    {
        var fresh = new UiState();
        PaneWidth = fresh.PaneWidth;
        PaneVisible = fresh.PaneVisible;
        CoverView = fresh.CoverView;
        Sort = fresh.Sort;
        WindowBounds = fresh.WindowBounds;
        Maximized = fresh.Maximized;
    }

    public static UiState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize(File.ReadAllText(path), StorageJson.Default.UiState) ?? new UiState();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall back to defaults: a damaged or locked file shouldn't stop the app from opening.
        }
        return new UiState();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written aside and swapped in, so a copy starting meanwhile never reads half a file.
            // A name of its own, so two copies saving at once (during a restart) don't share one.
            temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, StorageJson.Default.UiState));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth an error message: the next save tries again. Don't leave the copy behind.
            try { if (temp != null) File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
