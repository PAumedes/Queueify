using System.Text.Json.Serialization;

namespace Queueify.Services;

/// <summary>One album on the wire between a PC and the Queueify web server (ADR-0010).</summary>
public sealed record SyncAlbum(
    string Uid, string Artist, string Album, int? Year, string? Source, string? CoverUrl,
    string? SpotifyId, string? TidalId, DateTime AddedAt, DateTime? ListenedAt, double Rating,
    DateTime UpdatedAt, bool Deleted);

/// <summary>The settings shared between PCs (keys, Play app, country), as in <c>AppSettings.CopySharedFrom</c>.</summary>
public sealed record SyncSettings(
    string SpotifyClientId, string SpotifyClientSecret, string TidalClientId, string TidalClientSecret,
    string PreferredService, string CountryCode, DateTime UpdatedAt);

public sealed record SyncRequest(DateTime? Since, List<SyncAlbum> Albums, SyncSettings? Settings);

public sealed record SyncResponse(DateTime ServerTime, List<SyncAlbum> Albums, SyncSettings? Settings);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(SyncRequest))]
[JsonSerializable(typeof(SyncResponse))]
public sealed partial class SyncJson : JsonSerializerContext;

/// <summary>The text that connects a PC to a web server: <c>queueify-web|https://host|token</c>.</summary>
public static class SyncConnect
{
    private const string Prefix = "queueify-web";

    public static string Format(string baseUrl, string token) => $"{Prefix}|{baseUrl.TrimEnd('/')}|{token}";

    public static bool TryParse(string text, out string baseUrl, out string token)
    {
        baseUrl = token = "";
        var parts = text.Trim().Split('|');
        if (parts.Length != 3 || parts[0] != Prefix || parts[2].Length == 0 || parts[2].Any(char.IsWhiteSpace)) return false;
        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri)) return false;
        bool secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!secure || uri.UserInfo.Length > 0) return false;
        baseUrl = uri.GetLeftPart(UriPartial.Authority);
        token = parts[2];
        return true;
    }
}
