using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Models;
using Queueify.Web.Data;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests;

/// <summary>The whole app in-process, on scratch data: sign-in rules and what each user can see.</summary>
public sealed partial class WebAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private WebApplicationFactory<Program> Factory(bool allowRegistration = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            Directory.CreateDirectory(_root);
            b.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(_root, "app.db")}");
            b.UseSetting("Queueify:DataRoot", Path.Combine(_root, "users"));
            b.UseSetting("Queueify:AllowRegistration", allowRegistration ? "true" : "false");
        });

    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email)
    {
        var page = await client.GetStringAsync("/Account/Register", TestContext.Current.CancellationToken);
        var token = Token().Match(page).Groups[1].Value;
        Assert.NotEmpty(token);
        return await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "register",
            ["Input.Email"] = email,
            ["Input.Password"] = "Passw0rd!x",
            ["Input.ConfirmPassword"] = "Passw0rd!x",
        }), TestContext.Current.CancellationToken);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [Theory]
    [InlineData("/")]
    [InlineData("/add")]
    [InlineData("/listened")]
    public async Task Anonymous_visitors_are_sent_to_the_login_page(string path)
    {
        using var factory = Factory();
        var response = await Client(factory).GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task The_login_page_is_open_and_has_no_bootstrap()
    {
        using var factory = Factory();
        var html = await Client(factory).GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Contains("q-btn", html);
        Assert.DoesNotContain("btn-primary", html);
        Assert.DoesNotContain("form-control", html);
        Assert.DoesNotContain("bootstrap", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_first_account_can_register_and_is_signed_in_at_once()
    {
        using var factory = Factory();
        var client = Client(factory);

        var response = await RegisterAsync(client, "owner@example.com");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var home = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task After_the_owner_registration_is_closed_unless_the_server_opens_it()
    {
        using var factory = Factory(allowRegistration: false);
        await RegisterAsync(Client(factory), "owner@example.com");

        var second = Client(factory);
        var html = await second.GetStringAsync("/Account/Register", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Input.Email", html);

        // Posting the form anyway creates nothing.
        var token = Token().Match(await second.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken)).Groups[1].Value;
        await second.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "register", ["Input.Email"] = "intruder@example.com",
            ["Input.Password"] = "Passw0rd!x", ["Input.ConfirmPassword"] = "Passw0rd!x",
        }), TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        Assert.Single(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.ToList());
    }

    [Fact]
    public async Task With_registration_open_a_second_account_is_created()
    {
        using var factory = Factory(allowRegistration: true);
        await RegisterAsync(Client(factory), "owner@example.com");
        var response = await RegisterAsync(Client(factory), "friend@example.com");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.Count());
    }

    [Fact]
    public async Task Two_users_never_see_each_others_queue()
    {
        using var factory = Factory(allowRegistration: true);
        var alice = Client(factory);
        var bob = Client(factory);
        await RegisterAsync(alice, "alice@example.com");
        await RegisterAsync(bob, "bob@example.com");

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var stores = scope.ServiceProvider.GetRequiredService<UserStores>();
            var aliceId = (await users.FindByEmailAsync("alice@example.com"))!.Id;
            var bobId = (await users.FindByEmailAsync("bob@example.com"))!.Id;
            stores.QueueOf(aliceId).Insert(new BacklogItem { Artist = "Soda Stereo", Album = "AliceOnly Signos", AddedAt = DateTime.Now });
            stores.QueueOf(bobId).Insert(new BacklogItem { Artist = "Spinetta", Album = "BobOnly Artaud", AddedAt = DateTime.Now });
        }

        var aliceHtml = await alice.GetStringAsync("/", TestContext.Current.CancellationToken);
        var bobHtml = await bob.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("AliceOnly Signos", aliceHtml);
        Assert.DoesNotContain("BobOnly", aliceHtml);
        Assert.Contains("BobOnly Artaud", bobHtml);
        Assert.DoesNotContain("AliceOnly", bobHtml);
    }

    [Fact]
    public async Task The_home_page_lists_every_page_in_the_navigation()
    {
        using var factory = Factory();
        var client = Client(factory);
        await RegisterAsync(client, "owner@example.com");

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        foreach (var href in new[] { "href=\"\"", "href=\"add\"", "href=\"listened\"", "href=\"photo\"", "href=\"help\"", "href=\"settings\"", "href=\"connect\"", "Account/Logout" })
            Assert.Contains(href, html);
    }

    [Theory]
    [InlineData("es", "Para escuchar")]
    [InlineData("en", "To listen")]
    public async Task The_language_cookie_decides_the_text(string language, string expected)
    {
        using var factory = Factory();
        var client = Client(factory);
        await RegisterAsync(client, "owner@example.com");
        client.DefaultRequestHeaders.Add("Cookie", $"qlang={language}");

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains(expected, html);
        Assert.Contains($"<html lang=\"{language}\"", html);
    }

    [Fact]
    public async Task Without_a_cookie_the_browsers_language_decides()
    {
        using var factory = Factory();
        var client = Client(factory);
        client.DefaultRequestHeaders.Add("Accept-Language", "es-AR,es;q=0.9");

        var html = await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        Assert.Contains("Entrar", html);
    }

    [Fact]
    public async Task Signing_out_is_a_post_only()
    {
        using var factory = Factory();
        var client = Client(factory);
        await RegisterAsync(client, "owner@example.com");

        var get = await client.GetAsync("/Account/Logout", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.OK, get.StatusCode);

        // A POST without the antiforgery token is refused.
        var post = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["ReturnUrl"] = "" }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task The_preferences_form_sets_cookies_and_never_redirects_off_site()
    {
        using var factory = Factory();
        var client = Client(factory);
        var token = Token().Match(await client.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken)).Groups[1].Value;

        var response = await client.PostAsync("/prefs", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["language"] = "es", ["theme"] = "dark", ["returnUrl"] = "//evil.example",
        }), TestContext.Current.CancellationToken);

        Assert.Equal("/", response.Headers.Location!.ToString());
        var cookies = string.Join(";", response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("qlang=es", cookies);
        Assert.Contains("qtheme=dark", cookies);
    }

    [Fact]
    public async Task The_app_is_installable()
    {
        using var factory = Factory();
        var client = Client(factory);

        var manifest = await client.GetAsync("/manifest.webmanifest", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        var json = await manifest.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"display\": \"standalone\"", json);
        Assert.Contains("icon-512.png", json);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/sw.js", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/icons/icon-512.png", TestContext.Current.CancellationToken)).StatusCode);
    }
}
