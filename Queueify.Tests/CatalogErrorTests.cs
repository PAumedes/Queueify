using System.Net;
using System.Text;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>Only a refused key should make Settings say "Check keys"; busy or broken servers shouldn't.</summary>
public class CatalogErrorTests
{
    private sealed class FakeApi(HttpStatusCode tokenStatus, HttpStatusCode searchStatus, string searchBody = "{}") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            bool token = request.RequestUri!.ToString().Contains("token");
            var status = token ? tokenStatus : searchStatus;
            var body = token && status == HttpStatusCode.OK ? """{"access_token":"t","expires_in":3600}""" : searchBody;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static Task<CatalogException> Spotify(HttpStatusCode token, HttpStatusCode search, string body = "{}") =>
        Assert.ThrowsAsync<CatalogException>(() =>
            new SpotifyCatalog(new HttpClient(new FakeApi(token, search, body)),
                () => new AppSettings { SpotifyClientId = "id", SpotifyClientSecret = "s" }).SearchAsync("x", CancellationToken.None));

    private static Task<CatalogException> Tidal(HttpStatusCode token, HttpStatusCode search) =>
        Assert.ThrowsAsync<CatalogException>(() =>
            new TidalCatalog(new HttpClient(new FakeApi(token, search)),
                () => new AppSettings { TidalClientId = "id", TidalClientSecret = "s" }).SearchAsync("x", CancellationToken.None));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task RefusedKeys_AreReportedAsSuch(HttpStatusCode tokenStatus)
    {
        Assert.True((await Spotify(tokenStatus, HttpStatusCode.OK)).KeysRejected);
        Assert.True((await Tidal(tokenStatus, HttpStatusCode.OK)).KeysRejected);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task BusyOrBrokenServers_DontBlameTheKeys(HttpStatusCode status)
    {
        Assert.False((await Spotify(HttpStatusCode.OK, status)).KeysRejected);
        Assert.False((await Tidal(HttpStatusCode.OK, status)).KeysRejected);
        Assert.False((await Spotify(status, HttpStatusCode.OK)).KeysRejected);
        Assert.False((await Tidal(status, HttpStatusCode.OK)).KeysRejected);
    }

    [Fact]
    public async Task SpotifyQuotaExceeded_DoesntBlameTheKeys()
    {
        var ex = await Spotify(HttpStatusCode.OK, HttpStatusCode.TooManyRequests, """{"error":"QUOTA_EXCEEDED"}""");
        Assert.False(ex.KeysRejected);
        Assert.Contains("quota", ex.Message);
    }
}
