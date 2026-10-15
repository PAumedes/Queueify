using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

    private const string Password = "Passw0rd!-secret";

    private readonly SqliteConnection _db = new("DataSource=:memory:");

    private IRenderedComponent<ConnectPc> Open(string userId, string? publicUrl = null)
    {
        _db.Open();
        Services.AddDbContextFactory<ApplicationDbContext>(o => o.UseSqlite(_db));
        Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext());
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(publicUrl is null ? [] : new Dictionary<string, string?> { ["Queueify:PublicBaseUrl"] = publicUrl }).Build());
        Services.AddScoped<PersonalAccessTokens>();
        Services.AddScoped<Reauthentication>();
        Services.AddLogging();
        Services.AddIdentityCore<ApplicationUser>(o => o.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3).AddEntityFrameworkStores<ApplicationDbContext>();   // same model as the app: the model is cached per process
        Services.AddSingleton<AuthenticationStateProvider>(new SignedIn(userId));
        using (var scope = Services.BuildServiceProvider().CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.Database.EnsureCreated();
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            Assert.True(users.CreateAsync(new ApplicationUser { Id = userId, UserName = "u" }, Password).GetAwaiter().GetResult().Succeeded);
        }
        return Render<ConnectPc>();
    }

    [Fact]
    public void Creating_a_token_shows_the_connect_text_once_and_lists_it()
    {
        var page = Open("user-1");
        page.Find("#token-name").Input("Studio PC");
        page.Find("#token-password").Input(Password);
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
    public void A_wrong_password_makes_no_token_and_says_so()
    {
        var page = Open("user-2");
        page.Find("#token-name").Input("Studio PC");
        page.Find("#token-password").Input("not-the-password");
        page.Find("form").Submit();

        Assert.Empty(page.FindAll("#connect-text"));
        Assert.Contains(Loc.T("web.acct.m.bad_pw"), page.Markup);
        Assert.Empty(page.FindAll("li.q-row"));
    }

    [Fact]
    public void An_empty_password_makes_no_token()
    {
        var page = Open("user-3");
        page.Find("#token-name").Input("Studio PC");
        page.Find("form").Submit();

        Assert.Empty(page.FindAll("#connect-text"));
        Assert.Empty(page.FindAll("li.q-row"));
    }

    [Fact]
    public void Wrong_passwords_here_count_towards_the_accounts_lockout()
    {
        var page = Open("user-4");
        for (var i = 0; i < 6; i++)
        {
            page.Find("#token-name").Input("PC");
            page.Find("#token-password").Input("wrong-" + i);
            page.Find("form").Submit();
        }

        page.Find("#token-name").Input("PC");
        page.Find("#token-password").Input(Password);
        page.Find("form").Submit();
        Assert.Empty(page.FindAll("#connect-text"));
    }

    [Fact]
    public void The_connect_text_uses_the_configured_public_address_not_the_one_the_request_claims()
    {
        var page = Open("user-5", publicUrl: "https://queue.example.org");
        page.Find("#token-name").Input("PC");
        page.Find("#token-password").Input(Password);
        page.Find("form").Submit();

        Assert.StartsWith("queueify-web|https://queue.example.org|qw_", page.Find("#connect-text").GetAttribute("value")!);
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
