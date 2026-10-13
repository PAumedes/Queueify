using System.Reflection;

namespace Queueify.Services;

/// <summary>What the About section shows.</summary>
public static class AppInfo
{
    public const string Name = "Queueify";

    public static string Tagline => Loc.T("about.tagline");

    /// <summary>"1.0.0", from the project's &lt;Version&gt;.</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static IReadOnlyList<string> Credits =>
    [
        Loc.T("about.credit.search"),
        Loc.T("about.credit.ocr"),
        Loc.T("about.credit.sync"),
        Loc.T("about.credit.logos"),
        Loc.T("about.credit.built"),
    ];
}
