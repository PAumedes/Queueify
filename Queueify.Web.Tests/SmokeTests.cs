using Microsoft.AspNetCore.Mvc.Testing;
using Queueify.Web.Tests.Support;

namespace Queueify.Web.Tests;

public sealed class SmokeTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-smoke-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    // Its own data folder and account database: the smoke test must never create files in the source tree.
    public SmokeTests(WebApplicationFactory<Program> factory) => _factory = ScratchHost.On(factory, _root);

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Health_endpoint_answers_without_signing_in()
    {
        var response = await _factory.CreateClient().GetAsync("/healthz", TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode);
    }
}
