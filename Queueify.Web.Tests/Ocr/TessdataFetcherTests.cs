using System.Net;
using System.Security.Cryptography;
using Queueify.Web.Ocr;

namespace Queueify.Web.Tests.Ocr;

public sealed class TessdataFetcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qt-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class FakeHandler(Func<Uri, byte[]?> serve) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(serve(request.RequestUri!) is { } body
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    private static readonly byte[] Spa = "spanish model"u8.ToArray();
    private static readonly byte[] Eng = "english model"u8.ToArray();

    private (TessdataFetcher Fetcher, FakeHandler Handler) Make(Func<Uri, byte[]?>? serve = null, string configured = "")
    {
        var handler = new FakeHandler(serve ?? (u => u.AbsolutePath.EndsWith("spa.traineddata") ? Spa : Eng));
        var files = new[] { new TessdataFile("spa", "https://example.test/spa.traineddata", Sha(Spa)), new TessdataFile("eng", "https://example.test/eng.traineddata", Sha(Eng)) };
        return (new TessdataFetcher(new HttpClient(handler), _dir, configured, files), handler);
    }

    [Fact]
    public async Task Downloads_both_languages_once_and_returns_the_folder()
    {
        var (fetcher, handler) = Make();

        Assert.Equal(_dir, await fetcher.EnsureAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Spa, await File.ReadAllBytesAsync(Path.Combine(_dir, "spa.traineddata"), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests.Count);

        await fetcher.EnsureAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_download_whose_checksum_differs_is_never_kept()
    {
        var (fetcher, _) = Make(u => "tampered"u8.ToArray());

        await Assert.ThrowsAsync<PhotoException>(() => fetcher.EnsureAsync(TestContext.Current.CancellationToken));

        Assert.Empty(Directory.Exists(_dir) ? Directory.GetFiles(_dir) : []);
    }

    [Fact]
    public async Task A_failed_download_is_retried_on_the_next_call()
    {
        var up = false;
        var (fetcher, _) = Make(u => up ? (u.AbsolutePath.EndsWith("spa.traineddata") ? Spa : Eng) : null);

        await Assert.ThrowsAsync<PhotoException>(() => fetcher.EnsureAsync(TestContext.Current.CancellationToken));
        up = true;
        Assert.Equal(_dir, await fetcher.EnsureAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_damaged_local_copy_is_replaced()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "spa.traineddata"), "corrupt", TestContext.Current.CancellationToken);
        var (fetcher, _) = Make();

        await fetcher.EnsureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Spa, await File.ReadAllBytesAsync(Path.Combine(_dir, "spa.traineddata"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_administrators_folder_is_used_as_it_is_and_nothing_is_downloaded()
    {
        var (fetcher, handler) = Make(configured: "/srv/tessdata");

        Assert.Equal("/srv/tessdata", await fetcher.EnsureAsync(TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void The_pinned_downloads_come_from_tessdata_fast_with_a_checksum()
    {
        Assert.All(TessdataFetcher.Pinned, f =>
        {
            Assert.StartsWith("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/", f.Url);
            Assert.Equal(64, f.Sha256.Length);
        });
        Assert.Equal(["spa", "eng"], TessdataFetcher.Pinned.Select(f => f.Language));
    }
}
