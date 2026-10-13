using System.Net;
using System.Text;
using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>
/// "Save and test" must test the keys the user just typed: a token fetched with the old secret
/// may not be reused after the secret changes.
/// </summary>
public class CatalogTokenTests
{
    /// <summary>Answers token requests with a fresh token and searches with an empty result.</summary>
    private sealed class FakeApi : HttpMessageHandler
    {
        public List<string> TokenAuthorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            string body;
            if (url.Contains("token"))
            {
                TokenAuthorizations.Add(request.Headers.Authorization!.Parameter!);
                body = """{"access_token":"t","expires_in":3600}""";
            }
            else
            {
                body = url.Contains("spotify")
                    ? """{"albums":{"items":[]}}"""
                    : """{"data":{"relationships":{"albums":{"data":[]}}},"included":[]}""";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Spotify_FetchesANewToken_WhenOnlyTheSecretChanged()
    {
        var api = new FakeApi();
        var settings = new AppSettings { SpotifyClientId = "id", SpotifyClientSecret = "old" };
        var catalog = new SpotifyCatalog(new HttpClient(api), () => settings);

        await catalog.SearchAsync("x", CancellationToken.None);
        settings.SpotifyClientSecret = "new";
        await catalog.SearchAsync("x", CancellationToken.None);

        Assert.Equal(2, api.TokenAuthorizations.Count);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("id:new")), api.TokenAuthorizations[1]);
    }

    [Fact]
    public async Task Tidal_FetchesANewToken_WhenOnlyTheSecretChanged()
    {
        var api = new FakeApi();
        var settings = new AppSettings { TidalClientId = "id", TidalClientSecret = "old" };
        var catalog = new TidalCatalog(new HttpClient(api), () => settings);

        await catalog.SearchAsync("x", CancellationToken.None);
        settings.TidalClientSecret = "new";
        await catalog.SearchAsync("x", CancellationToken.None);

        Assert.Equal(2, api.TokenAuthorizations.Count);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("id:new")), api.TokenAuthorizations[1]);
    }

    [Fact]
    public async Task Spotify_ReusesTheToken_WhileTheKeysStayTheSame()
    {
        var api = new FakeApi();
        var settings = new AppSettings { SpotifyClientId = "id", SpotifyClientSecret = "s" };
        var catalog = new SpotifyCatalog(new HttpClient(api), () => settings);

        await catalog.SearchAsync("x", CancellationToken.None);
        await catalog.SearchAsync("y", CancellationToken.None);

        Assert.Single(api.TokenAuthorizations);
    }
}
