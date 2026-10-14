using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Queueify.Services;

/// <summary>A release Queueify can update to: its exe and the SHA-256 GitHub lists for it.</summary>
public sealed record AppRelease(string Version, Uri Download, string Sha256, Uri Page);

/// <summary>An update check or download that failed, with a message for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where the latest release is described, and where its download may come from.</summary>
public sealed record UpdateSource(Uri Latest, string DownloadPrefix)
{
    public static UpdateSource GitHub { get; } = new(
        new Uri("https://api.github.com/repos/PAumedes/Queueify/releases/latest"),
        "https://github.com/PAumedes/Queueify/releases/download/");

    /// <summary>The releases page, for when the app can't update itself.</summary>
    public static Uri Releases { get; } = new("https://github.com/PAumedes/Queueify/releases/latest");

    /// <summary>
    /// GitHub, unless QUEUEIFY_UPDATE_SERVER names a server on this PC: the UI tests serve a release from
    /// one. Anything else is ignored, so the variable can't point updates elsewhere.
    /// </summary>
    public static UpdateSource For(string? testServer)
    {
        if (!Uri.TryCreate(testServer, UriKind.Absolute, out var server) || server.Scheme != Uri.UriSchemeHttp || !server.IsLoopback)
            return GitHub;
        var root = new Uri(server.GetLeftPart(UriPartial.Authority) + "/");
        return new UpdateSource(new Uri(root, "latest"), root.AbsoluteUri);
    }
}

/// <summary>
/// Updates the download's exe from the latest release: checks the version, downloads the new exe next to
/// the old one, checks its SHA-256 and swaps the two. The exe isn't in use while the app runs (the starter
/// has finished by then), so it can be replaced.
/// </summary>
public sealed class Updater(HttpClient http, UpdateSource source)
{
    /// <summary>Several times Queueify's size: anything bigger isn't a Queueify release.</summary>
    private const long MaxDownload = 256L << 20;

    /// <summary>The release in GitHub's answer, or null when it isn't one Queueify can install.</summary>
    public AppRelease? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False
                || root.TryGetProperty("prerelease", out var pre) && pre.ValueKind != JsonValueKind.False
                || !root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            var version = tag.GetString()!.TrimStart('v');
            if (!System.Version.TryParse(version, out _)) return null;
            var page = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
                && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var p) && p.Scheme == Uri.UriSchemeHttps
                ? p : UpdateSource.Releases;

            var exeName = $"Queueify-{version}-win-x64.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != exeName) continue;
                var url = Text(asset, "browser_download_url");
                var digest = Text(asset, "digest");
                if (url == null || !url.StartsWith(source.DownloadPrefix, StringComparison.Ordinal)
                    || !Uri.TryCreate(url, UriKind.Absolute, out var download)
                    || digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                    || !digest[7..].All(char.IsAsciiHexDigit))
                    return null;
                return new AppRelease(version, download, digest[7..].ToLowerInvariant(), page);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate, out var c) && System.Version.TryParse(current, out var v) && c > v;

    /// <summary>The latest release when it's newer than this version; null when this is the latest.</summary>
    public async Task<AppRelease?> CheckAsync(string currentVersion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Latest);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        string json;
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(Loc.F("update.http", $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim()));
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (Failed(ex, ct) is { } message)
        {
            throw new UpdateException(message, ex);
        }

        var release = Parse(json) ?? throw new UpdateException(Loc.T("update.unreadable"));
        return IsNewer(release.Version, currentVersion) ? release : null;
    }

    /// <summary>
    /// Downloads the release's exe next to <paramref name="exe"/> and returns its path once its SHA-256
    /// matches; anything else leaves no file behind.
    /// </summary>
    public async Task<string> DownloadAsync(AppRelease release, string exe, IProgress<double>? progress, CancellationToken ct)
    {
        var path = exe + ".download";
        try
        {
            using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(Loc.F("update.http", $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim()));
            var length = response.Content.Headers.ContentLength;
            if (length > MaxDownload) throw new UpdateException(Loc.T("update.too_big"));

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                while (await ReadAsync(body, buffer, ct) is var n and > 0)
                {
                    received += n;
                    if (received > MaxDownload) throw new UpdateException(Loc.T("update.too_big"));
                    hash.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    if (length > 0) progress?.Report(Math.Min(1.0, (double)received / length.Value));
                }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
                throw new UpdateException(Loc.T("update.damaged"));
            progress?.Report(1.0);
            return path;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            if (ex is UpdateException) throw;
            // Reading the download fails as an UpdateException (see ReadAsync); what's left is the file.
            if (ex is IOException or UnauthorizedAccessException)
                throw new UpdateException(Loc.F("update.no_write", Path.GetDirectoryName(exe)), ex);
            if (Failed(ex, ct) is { } message) throw new UpdateException(message, ex);
            throw;
        }
    }

    /// <summary>
    /// Puts the downloaded exe in place of the old one, which stays as "&lt;exe&gt;.old" until
    /// <see cref="RemoveLeftovers"/>. If the new one can't be moved in, the old one goes back.
    /// </summary>
    public static void Replace(string exe, string downloaded)
    {
        var old = exe + ".old";
        try
        {
            File.Move(exe, old, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(downloaded);
            throw new UpdateException(Loc.F("update.locked", ex.Message), ex);
        }
        try
        {
            File.Move(downloaded, exe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(downloaded);
            try
            {
                File.Move(old, exe);
            }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(Loc.F("update.halfway", Path.GetFileName(old), Path.GetFileName(exe)), back);
            }
            throw new UpdateException(Loc.F("update.locked", ex.Message), ex);
        }
    }

    private static async Task<int> ReadAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        try
        {
            return await body.ReadAsync(buffer, ct);
        }
        catch (IOException ex)
        {
            throw new UpdateException(Loc.F("update.unreachable", ex.Message), ex);
        }
    }

    /// <summary>Removes the exe an update replaced and a download that never finished; in use, they wait.</summary>
    public static void RemoveLeftovers(string exe)
    {
        TryDelete(exe + ".old");
        TryDelete(exe + ".download");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The message for a network failure; null for the caller's own cancellation and anything else.</summary>
    private static string? Failed(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => null,
        OperationCanceledException => Loc.T("update.timeout"),
        HttpRequestException => Loc.F("update.unreachable", ex.Message),
        _ => null,
    };
}
