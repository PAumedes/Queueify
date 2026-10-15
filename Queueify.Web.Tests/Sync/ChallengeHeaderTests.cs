using System.Net;

namespace Queueify.Web.Tests.Sync;

/// <summary>The empty 401 and 429 answers still tell a client what to do next.</summary>
public sealed class ChallengeHeaderTests
{
    private sealed class Strict() : SyncFactory(new() { ["Queueify:Sync:RequestsPerMinute"] = "1" });

    [Fact]
    public async Task A_refused_token_gets_the_bearer_challenge()
    {
        await using var app = new SyncFactory();
        using var response = await app.CreateClient().SendAsync(SyncFactory.Post("not-a-token", "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
    }

    [Fact]
    public async Task A_throttled_client_is_told_when_to_come_back()
    {
        await using var app = new Strict();
        using var first = await app.CreateClient().SendAsync(SyncFactory.Post("x", "{}"), TestContext.Current.CancellationToken);
        using var second = await app.CreateClient().SendAsync(SyncFactory.Post("x", "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), second.Headers.RetryAfter?.Delta);
        Assert.Empty(second.Headers.WwwAuthenticate);
    }
}
