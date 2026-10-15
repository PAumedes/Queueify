using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Queueify.Web.Services;

namespace Queueify.Web.Tests;

public sealed class PublicUrlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-url-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static IConfiguration Config(string? url) =>
        new ConfigurationBuilder().AddInMemoryCollection(url is null ? [] : new Dictionary<string, string?> { ["Queueify:PublicBaseUrl"] = url }).Build();

    [Theory]
    [InlineData("https://queue.example.org", "https://queue.example.org")]
    [InlineData("https://queue.example.org/", "https://queue.example.org")]
    [InlineData("https://queue.example.org:8443/some/path?x=1", "https://queue.example.org:8443")]
    [InlineData("http://localhost:5147", "http://localhost:5147")]
    public void A_configured_public_address_is_reduced_to_its_origin(string configured, string expected) =>
        Assert.Equal(expected, PublicUrl.Of(Config(configured)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("queue.example.org")]
    [InlineData("http://queue.example.org")]       // a token would travel in the clear
    [InlineData("ftp://queue.example.org")]
    [InlineData("https://user:pass@queue.example.org")]
    public void Anything_else_is_not_a_public_address(string? configured) => Assert.Null(PublicUrl.Of(Config(configured)));

    private async Task<string> CspAsync(string? publicUrl)
    {
        Directory.CreateDirectory(_root);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Queueify:DataDir", Path.Combine(_root, "data"));
            if (publicUrl is not null) b.UseSetting("Queueify:PublicBaseUrl", publicUrl);
        });
        using var response = await factory.CreateClient().GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        return response.Headers.GetValues("Content-Security-Policy").Single();
    }

    private static string Directive(string csp, string name) =>
        csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith(name + " ", StringComparison.Ordinal));

    [Fact]
    public async Task The_circuit_may_only_talk_to_this_site()
    {
        var csp = await CspAsync(null);
        Assert.Equal("connect-src 'self'", Directive(csp, "connect-src"));
    }

    [Fact]
    public async Task A_configured_public_address_is_added_as_its_websocket_for_older_browsers()
    {
        var csp = await CspAsync("https://queue.example.org");
        Assert.Equal("connect-src 'self' wss://queue.example.org", Directive(csp, "connect-src"));
    }

    [Fact]
    public async Task Pictures_made_in_the_browser_can_be_shown_but_scripts_still_only_come_from_the_site()
    {
        var csp = await CspAsync(null);
        Assert.Contains("blob:", Directive(csp, "img-src").Split(' '));
        Assert.Equal("script-src 'self'", Directive(csp, "script-src"));
    }
}
