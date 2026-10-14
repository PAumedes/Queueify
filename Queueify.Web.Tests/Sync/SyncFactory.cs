using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Services;
using Queueify.Web.Data;
using Queueify.Web.Storage;
using Queueify.Web.Sync;

namespace Queueify.Web.Tests.Sync;

/// <summary>The real app on a scratch data folder and a scratch Identity database; nothing of the user's is touched.</summary>
public class SyncFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string> _settings;

    public SyncFactory() : this([]) { }

    protected SyncFactory(Dictionary<string, string> settings) => _settings = settings;

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "qw-sync-" + Guid.NewGuid().ToString("N"));
    public string DataDir => Path.Combine(Dir, "data");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Queueify:DataDir", DataDir);
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(Dir, "identity.db")};Pooling=False");
        // Every test request comes from the same address, so the limit is lifted unless a test is about it.
        builder.UseSetting("Queueify:Sync:RequestsPerMinute", "100000");
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
    }

    /// <summary>A confirmed user with one token; returns the user id and the token text.</summary>
    public async Task<(string UserId, string Token)> NewUserAsync(string name = "alice")
    {
        name += Guid.NewGuid().ToString("N")[..6];
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = name + "@example.org", Email = name + "@example.org", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, "Passw0rd!-" + name)).Succeeded);
        var created = await scope.ServiceProvider.GetRequiredService<PersonalAccessTokens>().CreateAsync(user.Id, "test PC");
        return (user.Id, created.Token);
    }

    public UserStores Stores => Services.GetRequiredService<UserStores>();

    public static HttpRequestMessage Post(string? token, string json, string path = "/api/sync")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<(HttpStatusCode Status, SyncResponse? Body)> SyncAsync(string? token, SyncRequest request)
    {
        using var response = await CreateClient().SendAsync(Post(token, System.Text.Json.JsonSerializer.Serialize(request, SyncJson.Default.SyncRequest)));
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.IsSuccessStatusCode ? System.Text.Json.JsonSerializer.Deserialize(text, SyncJson.Default.SyncResponse) : null);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
