using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Services;
using Queueify.Web.Components.Pages;
using Queueify.Web.Data;
using Queueify.Web.Sync;

namespace Queueify.Web.Tests.Sync;

public sealed class ConnectPcTests : BunitContext
{
    private sealed class SignedIn(string id) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"))));
    }

    private readonly SqliteConnection _db = new("DataSource=:memory:");

    private IRenderedComponent<ConnectPc> Open(string userId)
    {
        _db.Open();
        Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_db));
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<PersonalAccessTokens>();
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(userId));
        using (var scope = Services.BuildServiceProvider().CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.Database.EnsureCreated();
            context.Users.Add(new ApplicationUser { Id = userId, UserName = "u", NormalizedUserName = "U", SecurityStamp = "s" });
            context.SaveChanges();
        }
        return Render<ConnectPc>();
    }

    [Fact]
    public void Creating_a_token_shows_the_connect_text_once_and_lists_it()
    {
        var page = Open("user-1");
        page.Find("#token-name").Input("Studio PC");
        page.Find("form").Submit();

        var text = page.Find("#connect-text").GetAttribute("value")!;
        Assert.Matches("^queueify-web\\|http://localhost\\|qw_[A-Za-z0-9_-]{43}$", text);
        Assert.Contains("Studio PC", page.Markup);

        page.Find("li.q-row .q-actions button").Click();
        Assert.Empty(page.FindAll("#connect-text"));
        Assert.Empty(page.FindAll("li.q-row .q-actions button"));
        Assert.Contains(Loc.T("connect.revoked"), page.Markup);
        Assert.DoesNotContain("qw_", page.Markup);
    }

    [Fact]
    public void Every_text_on_the_page_exists_in_both_languages()
    {
        var razor = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Queueify.Web", "Components", "Pages", "ConnectPc.razor"));
        var keys = Regex.Matches(razor, "Loc\\.T\\(\"(connect\\.[a-z_.]+)\"\\)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, k => { Assert.True(Loc.English.ContainsKey(k), k); Assert.True(Loc.Spanish.ContainsKey(k), k); });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _db.Dispose();
    }
}

public sealed class ConnectPageAccessTests(SyncFactory app) : IClassFixture<SyncFactory>
{
    [Fact]
    public async Task Anonymous_visitors_never_get_the_token_form()
    {
        var client = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/connect");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token-name", body);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
