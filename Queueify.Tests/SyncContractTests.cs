using System.Text.Json;
using Queueify.Services;

namespace Queueify.Tests;

public sealed class SyncContractTests
{
    [Fact]
    public void A_request_round_trips_through_the_source_generated_json()
    {
        var request = new SyncRequest(
            new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc),
            [new SyncAlbum("u1", "Artist", "Album", 1999, "Rolling Stone", null, "sp", "ti",
                new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), null, -1, new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), false)],
            null);

        var json = JsonSerializer.Serialize(request, SyncJson.Default.SyncRequest);
        var back = JsonSerializer.Deserialize(json, SyncJson.Default.SyncRequest)!;

        Assert.Equal(request.Since, back.Since);
        Assert.Equal("Album", back.Albums.Single().Album);
        Assert.Contains("\"updatedAt\"", json);   // camelCase on the wire, as in the ADR
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var back = JsonSerializer.Deserialize("""{"serverTime":"2026-10-14T12:00:00Z","albums":[],"settings":null,"future":1}""", SyncJson.Default.SyncResponse);
        Assert.NotNull(back);
    }

    [Fact]
    public void The_connect_text_holds_the_server_and_the_token()
    {
        var text = SyncConnect.Format("https://q.example.com", "qw_abc");
        Assert.Equal("queueify-web|https://q.example.com|qw_abc", text);
        Assert.True(SyncConnect.TryParse(" " + text + " ", out var url, out var token));
        Assert.Equal("https://q.example.com", url);
        Assert.Equal("qw_abc", token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("queueify:proj:abc")]
    [InlineData("queueify-web|http://q.example.com|qw_abc")]      // plain http is refused, except localhost
    [InlineData("queueify-web|https://q.example.com|")]
    [InlineData("queueify-web|not a url|qw_abc")]
    public void Bad_connect_text_is_refused(string text) => Assert.False(SyncConnect.TryParse(text, out _, out _));

    [Fact]
    public void Localhost_may_use_http_for_development() =>
        Assert.True(SyncConnect.TryParse("queueify-web|http://localhost:5080|qw_abc", out _, out _));
}
