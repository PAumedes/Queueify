using System.Text.Json;

namespace Queueify.Services;

/// <summary>
/// Per-PC layout preferences (panel width, view mode, window position, quick tour). Never synced:
/// each PC has its own screen.
/// </summary>
public sealed class UiState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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

    public static UiState Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(path)) ?? new UiState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Fall back to defaults; layout preferences aren't worth an error message.
        }
        return new UiState();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException)
        {
            // Same as above: losing a layout preference is harmless.
        }
    }
}
