using Microsoft.AspNetCore.Mvc.Testing;

namespace Queueify.Web.Tests;

public sealed class SmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_endpoint_answers_without_signing_in()
    {
        var response = await factory.CreateClient().GetAsync("/healthz");
        Assert.True(response.IsSuccessStatusCode);
    }
}
