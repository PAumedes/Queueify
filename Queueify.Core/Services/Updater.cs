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

    /// <summary>Every version, for when the latest one doesn't work here.</summary>
    public static Uri AllReleases { get; } = new("https://github.com/PAumedes/Queueify/releases");

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
public sealed class Updater(HttpClient http, UpdateSource source, TimeSpan? stallTimeout = null)
{
    private readonly TimeSpan _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30);

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

            // Three parts, as AppInfo.Version shows it: anything else would be offered again after updating.
            var version = tag.GetString()!.TrimStart('v');
            if (!System.Version.TryParse(version, out var parsed) || parsed.Build < 0 || parsed.Revision >= 0) return null;
            var page = root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
                && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var p) && p.Scheme == Uri.UriSchemeHttps
                ? p : UpdateSource.Releases;

            var exeName = $"Queueify-{version}-win-x64.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != exeName) continue;
                var url = Text(asset, "browser_download_url");
                var digest = Text(asset, "digest");
                // Checked once read as an address, after "..", "%2e" and "\" are resolved.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var download)
                    || !download.AbsoluteUri.StartsWith(source.DownloadPrefix, StringComparison.Ordinal)
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

    /// <summary>Whether to offer a release: one the user skipped only when they asked (Check for updates).</summary>
    public static bool IsWanted(AppRelease release, string? skipped, bool asked) => asked || release.Version != skipped;

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
                throw new UpdateException(Loc.F("update.http", (int)response.StatusCode));
            // GitHub's JSON is UTF-8 whatever a proxy on the way says it is.
            json = System.Text.Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(ct));
        }
        catch (Exception ex) when (ex is not UpdateException && Failed(ex, ct) is { } message)
        {
            throw new UpdateException(message, ex);
        }

        var release = Parse(json) ?? throw new UpdateException(Loc.T("update.unreadable"));
        return IsNewer(release.Version, currentVersion) ? release : null;
    }

    /// <summary>
    /// Downloads the release's exe next to <paramref name="exe"/> and returns its path once its SHA-256
    /// matches; anything else leaves no file behind. Only the caller's own cancellation comes out as
    /// something other than an <see cref="UpdateException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(AppRelease release, string exe, IProgress<double>? progress, CancellationToken ct)
    {
        var path = exe + ".download";
        try
        {
            using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException(Loc.F("update.http", (int)response.StatusCode));
            var length = response.Content.Headers.ContentLength;
            if (length > MaxDownload) throw new UpdateException(Loc.T("update.too_big"));

            // Always a file of its own: one already there (or a link) may belong to someone else who can
            // write the folder, and would keep their rights to the new exe. If it can't go, no update.
            TryDelete(path);
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still there: another Queueify window is downloading, or someone else's file won't go.
                throw new UpdateException(File.Exists(path)
                    ? Loc.T("update.locked")
                    : Loc.F("update.no_write", Path.GetDirectoryName(exe)), ex);
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (file)
            await using (var body = await response.Content.ReadAsStreamAsync(ct))
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
            // Cancel pressed after the last read still counts: nothing is put in place.
            ct.ThrowIfCancellationRequested();
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
                throw new UpdateException(Loc.T("update.damaged"));
            progress?.Report(1.0);
            return path;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            if (ex is UpdateException) throw;
            // Reading the download fails as an UpdateException (see ReadAsync); what's left is writing the file.
            if (ex is IOException or UnauthorizedAccessException)
                throw new UpdateException(Loc.F("update.no_write", Path.GetDirectoryName(exe)), ex);
            if (Failed(ex, ct) is { } message) throw new UpdateException(message, ex);
            throw;
        }
    }

    /// <summary>
    /// One read of the download. A connection that sends nothing for a while gives up; a slow one that keeps
    /// sending doesn't (HttpClient's own timeout stops at the headers).
    /// </summary>
    private async Task<int> ReadAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stalled.CancelAfter(_stallTimeout);
        try
        {
            return await body.ReadAsync(buffer, stalled.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException(Failed(ex, ct)!, ex);
        }
    }
    /// <summary>
    /// Puts the downloaded exe in place of the old one, in one step: the name is never free, not even for a
    /// moment, so a file someone else drops in the folder can't take it, and no other name next to the exe
    /// is ever written (one could be a link to the user's own files). If it fails, the exe is as it was.
    /// </summary>
    public static void Replace(string exe, string downloaded)
    {
        var readOnly = File.Exists(exe) && File.GetAttributes(exe).HasFlag(FileAttributes.ReadOnly);
        try
        {
            // A read-only exe (copied from a CD or a locked-down share) couldn't be replaced.
            ClearReadOnly(exe);
            File.Move(downloaded, exe, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (readOnly) TrySetReadOnly(exe);
            TryDelete(downloaded);
            throw new UpdateException(Loc.T("update.locked"), ex);
        }
    }
    private static void TrySetReadOnly(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>Removes a download that never finished; one in use (another window downloading) waits.</summary>
    public static void RemoveLeftovers(string exe) => TryDelete(exe + ".download");

    private static void TryDelete(string path)
    {
        try
        {
            ClearReadOnly(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Only on the file itself: a link left in its place is never followed.</summary>
    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly) && !attributes.HasFlag(FileAttributes.ReparsePoint))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    // Plain words for the user: the system's own messages are English and technical. Only an unexpected
    // failure (a bug) shows it, as a clue.
    /// <summary>The message for a failure; null only for the caller's own cancellation, which goes on as it is.</summary>
    private static string? Failed(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => null,
        OperationCanceledException => Loc.T("update.timeout"),
        HttpRequestException or IOException => Loc.T("update.unreachable"),
        _ => Loc.F("update.failed", ex.Message),
    };
}
