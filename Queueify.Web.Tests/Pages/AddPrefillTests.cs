using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Services;
using Queueify.Web.Storage;
using Queueify.Web.Tests.Support;

namespace Queueify.Web.Tests.Pages;

public sealed class AddPrefillTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qf-" + Guid.NewGuid().ToString("N"));
    private readonly WebComponentTests.FakeCatalogs _catalogs = new();
    private readonly WebApplicationFactory<Program> _factory;

    public AddPrefillTests(WebApplicationFactory<Program> factory) =>
        _factory = ScratchHost.On(factory, _root, services => services.AddSingleton<ICatalogs>(_catalogs));

    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(_root, true); } catch (IOException) { } }

    [Fact]
    public async Task A_shared_search_link_does_not_call_the_catalogs_while_the_page_is_drawn_on_the_server()
    {
        new UserStores(_root).SaveSettings("alice", new AppSettings { SpotifyClientId = "id", SpotifyClientSecret = "s", TidalClientId = "id", TidalClientSecret = "s" });

        var html = await _factory.ClientFor("alice").GetStringAsync("/add?q=soda%20stereo%20signos", TestContext.Current.CancellationToken);

        // The text is in the box for the browser to see; the search itself waits for the live page, so it runs once.
        Assert.Contains("soda stereo signos", html);
        Assert.Empty(_catalogs.Queries);
    }
}
