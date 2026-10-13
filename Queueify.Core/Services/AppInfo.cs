using System.Reflection;

namespace Queueify.Services;

/// <summary>What the About section shows.</summary>
public static class AppInfo
{
    public const string Name = "Queueify";

    public const string Tagline = "A listen-later queue for the albums your magazines recommend.";

    /// <summary>"1.0.0", from the project's &lt;Version&gt;.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static IReadOnlyList<string> Credits { get; } =
    [
        "Album search: Spotify Web API and Tidal API. Queueify isn't affiliated with Spotify or Tidal.",
        "Text recognition: the OCR engine built into Windows.",
        "Sync between PCs: Google Firebase (Cloud Firestore), in your own project.",
        "Built with WinUI 3, the Windows App SDK and .NET.",
    ];
}
