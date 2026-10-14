using System.Net;
using System.Security.Cryptography;
using System.Text;
using Queueify.Services;

namespace Queueify.Tests;

// Updates come from the latest GitHub release: its exe replaces the one the user started, after its
// SHA-256 matches the one GitHub lists for it.
public sealed class UpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly byte[] NewExe = Encoding.ASCII.GetBytes("MZ the new Queueify");
    private static string Sha256Of(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static string LatestJson(string tag = "v1.4.0", string? digest = null, string? url = null, string? name = null,
        bool draft = false, bool prerelease = false)
    {
        var version = tag.TrimStart('v');
        name ??= $"Queueify-{version}-win-x64.exe";
        url ??= $"https://github.com/PAumedes/Queueify/releases/download/{tag}/{name}";
        digest ??= "sha256:" + Sha256Of(NewExe);
        return $$"""
            {
              "tag_name": "{{tag}}",
              "name": "Queueify {{tag}}",
              "html_url": "https://github.com/PAumedes/Queueify/releases/tag/{{tag}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "assets": [
                { "name": "notes.txt", "digest": "sha256:{{new string('0', 64)}}",
                  "browser_download_url": "https://github.com/PAumedes/Queueify/releases/download/{{tag}}/notes.txt" },
                { "name": "{{name}}", "size": {{NewExe.Length}}, "digest": "{{digest}}",
                  "browser_download_url": "{{url}}" }
              ]
            }
            """;
    }

    private static Updater GitHub(HttpMessageHandler? api = null, TimeSpan? stall = null) =>
        new(new HttpClient(api ?? new FakeGitHub(HttpStatusCode.OK, LatestJson())), UpdateSource.GitHub, stall);

    // ── What the latest release offers ──

    [Fact]
    public void A_release_gives_its_version_download_hash_and_page()
    {
        var release = GitHub().Parse(LatestJson());

        Assert.NotNull(release);
        Assert.Equal("1.4.0", release.Version);
        Assert.Equal("https://github.com/PAumedes/Queueify/releases/download/v1.4.0/Queueify-1.4.0-win-x64.exe", release.Download.AbsoluteUri);
        Assert.Equal(Sha256Of(NewExe), release.Sha256);
        Assert.Equal("https://github.com/PAumedes/Queueify/releases/tag/v1.4.0", release.Page.AbsoluteUri);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Drafts_and_previews_are_not_offered(bool draft, bool prerelease)
    {
        Assert.Null(GitHub().Parse(LatestJson(draft: draft, prerelease: prerelease)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:not-a-hash")]
    [InlineData("sha256:0123")]
    public void A_release_without_a_usable_hash_is_not_offered(string digest)
    {
        Assert.Null(GitHub().Parse(LatestJson(digest: digest)));
    }

    [Theory]
    [InlineData("https://example.com/Queueify-1.4.0-win-x64.exe")]
    [InlineData("http://github.com/PAumedes/Queueify/releases/download/v1.4.0/Queueify-1.4.0-win-x64.exe")]
    [InlineData("https://github.com/someone/Queueify/releases/download/v1.4.0/Queueify-1.4.0-win-x64.exe")]
    // Dot segments that would lead out of Queueify's releases once the address is read
    [InlineData("https://github.com/PAumedes/Queueify/releases/download/../../../../evil/repo/releases/download/v1.4.0/Queueify-1.4.0-win-x64.exe")]
    [InlineData("https://github.com/PAumedes/Queueify/releases/download/%2e%2e/%2e%2e/%2e%2e/%2e%2e/evil/Queueify-1.4.0-win-x64.exe")]
    [InlineData("https://github.com/PAumedes/Queueify/releases/download/..\\..\\..\\..\\evil\\Queueify-1.4.0-win-x64.exe")]
    public void A_download_from_anywhere_but_Queueifys_releases_is_not_offered(string url)
    {
        Assert.Null(GitHub().Parse(LatestJson(url: url)));
    }

    [Theory]
    [InlineData("Queueify-1.3.0-win-x64.exe")]   // another version's exe
    [InlineData("Queueify-1.4.0-win-x64.zip")]
    [InlineData("Queueify-1.4.0-win-arm64.exe")]
    public void Only_that_versions_exe_is_offered(string name)
    {
        Assert.Null(GitHub().Parse(LatestJson(name: name)));
    }

    [Theory]
    [InlineData("v1.4.0.1")]   // Queueify shows three parts: a fourth would be offered again after updating
    [InlineData("v1.4")]
    public void Only_three_part_versions_are_offered(string tag)
    {
        Assert.Null(GitHub().Parse(LatestJson(tag: tag)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"tag_name": 14}""")]
    [InlineData("""{"tag_name": "v1.4.0", "assets": "none"}""")]
    [InlineData("""{"tag_name": "latest", "assets": []}""")]
    public void An_answer_that_isnt_a_release_offers_nothing(string json)
    {
        Assert.Null(GitHub().Parse(json));
    }

    [Theory]
    [InlineData("1.4.0", "1.3.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.3.0", "1.3.0", false)]
    [InlineData("1.2.9", "1.3.0", false)]
    [InlineData("soon", "1.3.0", false)]
    public void Only_a_newer_version_is_an_update(string candidate, string current, bool newer)
    {
        Assert.Equal(newer, Updater.IsNewer(candidate, current));
    }

    // ── Asking GitHub ──

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        var release = await GitHub().CheckAsync("1.3.0", CancellationToken.None);

        Assert.Equal("1.4.0", release?.Version);
    }

    [Fact]
    public async Task The_same_version_is_no_update()
    {
        Assert.Null(await GitHub().CheckAsync("1.4.0", CancellationToken.None));
    }

    [Fact]
    public async Task GitHub_asks_who_is_asking_and_gets_its_json()
    {
        var api = new FakeGitHub(HttpStatusCode.OK, LatestJson());

        await GitHub(api).CheckAsync("1.3.0", CancellationToken.None);

        Assert.Equal("https://api.github.com/repos/PAumedes/Queueify/releases/latest", api.Requests.Single().RequestUri!.AbsoluteUri);
        Assert.Contains("application/vnd.github+json", api.Requests.Single().Headers.Accept.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]          // GitHub's limit of 60 questions an hour
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_check_says_so(HttpStatusCode status)
    {
        await Assert.ThrowsAsync<UpdateException>(() => GitHub(new FakeGitHub(status, "{}")).CheckAsync("1.3.0", CancellationToken.None));
    }

    [Fact]
    public async Task No_connection_says_so()
    {
        await Assert.ThrowsAsync<UpdateException>(() => GitHub(new FakeGitHub(new HttpRequestException("No such host"))).CheckAsync("1.3.0", CancellationToken.None));
    }

    [Fact]
    public async Task An_answer_in_a_character_set_NET_doesnt_know_still_reads()
    {
        // GitHub sends UTF-8; a proxy may label it oddly.
        var api = new FakeGitHub(HttpStatusCode.OK, LatestJson()) { ContentType = "application/json; charset=utf8" };

        Assert.Equal("1.4.0", (await GitHub(api).CheckAsync("1.3.0", CancellationToken.None))?.Version);
    }

    [Fact]
    public async Task Any_other_failure_while_checking_says_so()
    {
        var api = new FakeGitHub(new InvalidOperationException("Something unexpected"));

        await Assert.ThrowsAsync<UpdateException>(() => GitHub(api).CheckAsync("1.3.0", CancellationToken.None));
    }

    [Fact]
    public async Task No_answer_in_time_says_so()
    {
        // HttpClient's own timeout, not the caller cancelling.
        var api = new FakeGitHub(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => GitHub(api).CheckAsync("1.3.0", CancellationToken.None));
        Assert.Equal(Loc.T("update.timeout"), ex.Message);
    }

    [Fact]
    public async Task A_release_that_cant_be_offered_says_so()
    {
        // Better than "you have the latest version" when GitHub has a newer one without a hash.
        var api = new FakeGitHub(HttpStatusCode.OK, LatestJson(digest: ""));

        await Assert.ThrowsAsync<UpdateException>(() => GitHub(api).CheckAsync("1.3.0", CancellationToken.None));
    }

    // ── Downloading ──

    private string Exe()
    {
        var exe = Path.Combine(_dir, "Queueify (1).exe");
        File.WriteAllText(exe, "MZ the old Queueify");
        return exe;
    }

    private static AppRelease Release(string? sha256 = null) => new("1.4.0",
        new Uri("https://github.com/PAumedes/Queueify/releases/download/v1.4.0/Queueify-1.4.0-win-x64.exe"),
        sha256 ?? Sha256Of(NewExe), new Uri("https://github.com/PAumedes/Queueify/releases/tag/v1.4.0"));

    [Fact]
    public async Task The_new_exe_is_downloaded_next_to_the_old_one()
    {
        var exe = Exe();
        var progress = new List<double>();

        var downloaded = await GitHub(new FakeGitHub(HttpStatusCode.OK, NewExe)).DownloadAsync(Release(), exe, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.Equal(_dir, Path.GetDirectoryName(downloaded));
        Assert.Equal(NewExe, File.ReadAllBytes(downloaded));
        Assert.Equal("MZ the old Queueify", File.ReadAllText(exe));
        Assert.Equal(1.0, progress.Last());
    }

    [Fact]
    public async Task A_download_that_doesnt_match_the_release_is_thrown_away()
    {
        var exe = Exe();
        var tampered = Encoding.ASCII.GetBytes("MZ something else");

        await Assert.ThrowsAsync<UpdateException>(() =>
            GitHub(new FakeGitHub(HttpStatusCode.OK, tampered)).DownloadAsync(Release(), exe, null, CancellationToken.None));

        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_download_far_bigger_than_Queueify_is_refused()
    {
        var exe = Exe();
        var api = new FakeGitHub(HttpStatusCode.OK, NewExe) { ClaimedLength = 2L << 30 };

        await Assert.ThrowsAsync<UpdateException>(() => GitHub(api).DownloadAsync(Release(), exe, null, CancellationToken.None));

        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_failed_download_leaves_nothing_behind()
    {
        var exe = Exe();

        await Assert.ThrowsAsync<UpdateException>(() =>
            GitHub(new FakeGitHub(HttpStatusCode.NotFound, "{}")).DownloadAsync(Release(), exe, null, CancellationToken.None));

        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_file_already_named_like_the_download_is_never_written_into()
    {
        // In a folder others can add files to, someone could leave one there that they can still change.
        var exe = Exe();
        var planted = new FileInfo(exe + ".download");
        File.WriteAllText(planted.FullName, "planted");
        var acl = planted.GetAccessControl();
        var everyone = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(everyone, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        planted.SetAccessControl(acl);

        var downloaded = await GitHub(new FakeGitHub(HttpStatusCode.OK, NewExe)).DownloadAsync(Release(), exe, null, CancellationToken.None);

        var rules = new FileInfo(downloaded).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
        Assert.DoesNotContain(rules.Cast<System.Security.AccessControl.FileSystemAccessRule>(), r => r.IdentityReference == everyone && !r.IsInherited);
        Assert.Equal(NewExe, File.ReadAllBytes(downloaded));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("other")]
    public async Task A_download_breaking_off_says_so_and_leaves_nothing(string how)
    {
        var exe = Exe();
        Exception failure = how == "io" ? new IOException("The connection was reset") : new InvalidOperationException("Something unexpected");
        var api = new FakeGitHub(HttpStatusCode.OK, NewExe) { Body = () => new BrokenStream(NewExe[..5], failure) };

        await Assert.ThrowsAsync<UpdateException>(() => GitHub(api).DownloadAsync(Release(), exe, null, CancellationToken.None));

        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_download_that_stops_coming_gives_up()
    {
        var exe = Exe();
        var api = new FakeGitHub(HttpStatusCode.OK, NewExe) { Body = () => new BrokenStream(NewExe[..5], null) };

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            GitHub(api, stall: TimeSpan.FromMilliseconds(200)).DownloadAsync(Release(), exe, null, CancellationToken.None));

        Assert.Equal(Loc.T("update.timeout"), ex.Message);
        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Cancelling_a_download_is_passed_on_and_leaves_nothing()
    {
        var exe = Exe();
        var api = new FakeGitHub(HttpStatusCode.OK, NewExe) { Body = () => new BrokenStream(NewExe[..5], null) };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GitHub(api).DownloadAsync(Release(), exe, null, cancel.Token));

        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_download_already_running_in_another_window_isnt_called_a_folder_problem()
    {
        var exe = Exe();
        using var other = File.Open(exe + ".download", FileMode.CreateNew, FileAccess.Write, FileShare.None);

        var ex = await Assert.ThrowsAsync<UpdateException>(() =>
            GitHub(new FakeGitHub(HttpStatusCode.OK, NewExe)).DownloadAsync(Release(), exe, null, CancellationToken.None));

        Assert.NotEqual(Loc.F("update.no_write", _dir), ex.Message);
    }

    [Fact]
    public async Task A_folder_Queueify_cant_write_to_says_so()
    {
        var exe = Path.Combine(_dir, "gone", "Queueify.exe");

        await Assert.ThrowsAsync<UpdateException>(() =>
            GitHub(new FakeGitHub(HttpStatusCode.OK, NewExe)).DownloadAsync(Release(), exe, null, CancellationToken.None));
    }

    // ── Swapping ──

    private string Downloaded()
    {
        var path = Path.Combine(_dir, "Queueify (1).exe.download");
        File.WriteAllBytes(path, NewExe);
        return path;
    }

    [Fact]
    public void The_new_exe_takes_the_old_ones_name()
    {
        var exe = Exe();

        Updater.Replace(exe, Downloaded());

        Assert.Equal(NewExe, File.ReadAllBytes(exe));
        Assert.Equal("MZ the old Queueify", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + ".download"));
    }

    [Fact]
    public void An_old_exe_left_from_the_last_update_is_replaced()
    {
        var exe = Exe();
        File.WriteAllText(exe + ".old", "MZ an even older Queueify");

        Updater.Replace(exe, Downloaded());

        Assert.Equal("MZ the old Queueify", File.ReadAllText(exe + ".old"));
    }

    [Fact]
    public void When_the_new_exe_cant_be_moved_in_the_old_one_stays()
    {
        var exe = Exe();
        var downloaded = Downloaded();
        // An antivirus still scanning it, say.
        using var scanning = File.Open(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<UpdateException>(() => Updater.Replace(exe, downloaded));

        Assert.Equal("MZ the old Queueify", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void When_the_old_exe_cant_be_moved_nothing_changes()
    {
        var exe = Exe();
        var downloaded = Downloaded();
        using var held = File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<UpdateException>(() => Updater.Replace(exe, downloaded));

        Assert.Equal("MZ the old Queueify", File.ReadAllText(exe));
    }

    [Fact]
    public void A_read_only_exe_is_updated_and_its_old_copy_removed()
    {
        var exe = Exe();
        File.SetAttributes(exe, FileAttributes.ReadOnly);

        Updater.Replace(exe, Downloaded());
        Updater.RemoveLeftovers(exe);
        File.WriteAllText(exe + ".download", "the next one");
        Updater.Replace(exe, exe + ".download");

        Assert.Equal("the next one", File.ReadAllText(exe));
        Updater.RemoveLeftovers(exe);
        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public void The_old_exe_and_a_stray_download_are_removed_next_time()
    {
        var exe = Exe();
        File.WriteAllText(exe + ".old", "old");
        File.WriteAllText(exe + ".download", "half");

        Updater.RemoveLeftovers(exe);

        Assert.Equal([exe], Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_leftover_still_in_use_waits_for_another_time()
    {
        var exe = Exe();
        File.WriteAllText(exe + ".old", "old");
        using var held = File.Open(exe + ".old", FileMode.Open, FileAccess.Read, FileShare.Read);

        Updater.RemoveLeftovers(exe);

        Assert.True(File.Exists(exe + ".old"));
    }

    // ── Where updates come from ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/")]       // only a server on this PC, for the UI tests
    [InlineData("http://192.168.1.20:8123/")]
    [InlineData("file:///C:/releases/")]
    [InlineData("not a url")]
    public void Updates_come_from_GitHub(string? testServer)
    {
        Assert.Equal(UpdateSource.GitHub, UpdateSource.For(testServer));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8123/")]
    [InlineData("http://127.0.0.1:8123")]
    [InlineData("http://localhost:8123/")]
    public void The_UI_tests_can_serve_updates_from_this_PC(string testServer)
    {
        var source = UpdateSource.For(testServer);

        Assert.Equal(new Uri(new Uri(testServer), "latest"), source.Latest);
        var release = new Updater(new HttpClient(), source).Parse(LatestJson(url: new Uri(new Uri(testServer), "Queueify-1.4.0-win-x64.exe").AbsoluteUri));
        Assert.NotNull(release);
    }

    /// <summary>Gives some bytes, then fails with <paramref name="failure"/>, or never answers again when it's null.</summary>
    private sealed class BrokenStream(byte[] first, Exception? failure) : Stream
    {
        private bool _sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!_sent)
            {
                _sent = true;
                first.CopyTo(buffer);
                return first.Length;
            }
            if (failure != null) throw failure;
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly byte[] _body = [];
        private readonly Exception? _failure;

        public FakeGitHub(HttpStatusCode status, string body) : this(status, Encoding.UTF8.GetBytes(body)) { }
        public FakeGitHub(HttpStatusCode status, byte[] body) { _status = status; _body = body; }
        public FakeGitHub(Exception failure) => _failure = failure;

        public List<HttpRequestMessage> Requests { get; } = [];
        public long? ClaimedLength { get; init; }
        public string? ContentType { get; init; }
        public Func<Stream>? Body { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            if (_failure != null) throw _failure;
            HttpContent content = Body != null ? new StreamContent(Body()) : new ByteArrayContent(_body);
            if (ClaimedLength is { } length) content.Headers.ContentLength = length;
            if (ContentType != null) content.Headers.TryAddWithoutValidation("Content-Type", ContentType);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }
}
