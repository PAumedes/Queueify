using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
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
            b.UseSetting("Queueify:DataDir", Path.Combine(_root, "users"));
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
    public async Task The_registration_confirmation_page_tells_nothing_about_who_has_an_account()
    {
        using var factory = Factory();
        await RegisterAsync(Client(factory), "owner@example.com");
        var anonymous = Client(factory);

        var known = await anonymous.GetAsync("/Account/RegisterConfirmation?email=owner@example.com", TestContext.Current.CancellationToken);
        var unknown = await anonymous.GetAsync("/Account/RegisterConfirmation?email=nobody@example.com", TestContext.Current.CancellationToken);

        Assert.Equal(unknown.StatusCode, known.StatusCode);
        var knownHtml = await known.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var unknownHtml = await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // The page links to itself (skip link), so only the address in that link may differ.
        string Plain(string html, string address) => Regex.Replace(Regex.Replace(html.Replace(address, "X"), "<!--Blazor:.*?-->", ""), "[-A-Za-z0-9_+/=]{40,}", "T");   // antiforgery tokens and component state differ per response
        Assert.Equal(Plain(unknownHtml, "nobody@example.com"), Plain(knownHtml, "owner@example.com"));
        Assert.DoesNotContain("ConfirmEmail", knownHtml);
        Assert.DoesNotContain("userId", knownHtml);
    }

    [Fact]
    public async Task Deleting_the_only_account_does_not_hand_the_server_to_the_next_visitor()
    {
        using var factory = Factory(allowRegistration: false);
        await RegisterAsync(Client(factory), "owner@example.com");

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByEmailAsync("owner@example.com"))!)).Succeeded);
            Assert.Empty(users.Users.ToList());
        }

        var visitor = Client(factory);
        Assert.DoesNotContain("Input.Email", await visitor.GetStringAsync("/Account/Register", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_existing_server_without_the_marker_gets_it_the_first_time_the_gate_is_asked()
    {
        using var factory = Factory(allowRegistration: false);
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(new ApplicationUser { UserName = "old@example.com", Email = "old@example.com" }, "Passw0rd!x")).Succeeded);
            Assert.False(await scope.ServiceProvider.GetRequiredService<Queueify.Web.Services.RegistrationGate>().IsOpenAsync());
            await users.DeleteAsync((await users.FindByEmailAsync("old@example.com"))!);
            Assert.False(await scope.ServiceProvider.GetRequiredService<Queueify.Web.Services.RegistrationGate>().IsOpenAsync());
        }
    }

    private WebApplicationFactory<Program> ProductionFactory(bool allowInsecureCookies = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            Directory.CreateDirectory(_root);
            b.UseEnvironment("Production");
            b.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(_root, "prod.db")}");
            b.UseSetting("Queueify:DataDir", Path.Combine(_root, "prod-users"));
            if (allowInsecureCookies) b.UseSetting("Queueify:AllowInsecureCookies", "true");
        });

    // A secure cookie is not stored by a client talking http, so the test hands it back by hand, like a browser behind the proxy would.
    private static HttpClient PlainHttpClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false, BaseAddress = new Uri("http://localhost") });

    private static void SendBack(HttpClient client, IEnumerable<string> setCookies) =>
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", setCookies.Select(c => c.Split(';')[0])));

    private static async Task<List<string>> CookiesSetBySigningUpOverPlainHttpAsync(WebApplicationFactory<Program> factory)
    {
        var client = PlainHttpClient(factory);
        var cookies = new List<string>();
        var page = await client.GetAsync("/Account/Register", TestContext.Current.CancellationToken);
        cookies.AddRange(page.Headers.TryGetValues("Set-Cookie", out var first) ? first : []);
        SendBack(client, cookies);
        var token = Token().Match(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Groups[1].Value;
        var signUp = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "register", ["Input.Email"] = "owner@example.com",
            ["Input.Password"] = "Passw0rd!x", ["Input.ConfirmPassword"] = "Passw0rd!x",
        }), TestContext.Current.CancellationToken);
        cookies.AddRange(signUp.Headers.TryGetValues("Set-Cookie", out var second) ? second : []);
        return cookies;
    }

    [Fact]
    public async Task In_production_the_sign_in_and_antiforgery_cookies_are_secure_even_when_the_proxy_hides_https()
    {
        using var factory = ProductionFactory();
        var cookies = await CookiesSetBySigningUpOverPlainHttpAsync(factory);

        var session = Assert.Single(cookies, c => c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        var antiforgery = Assert.Single(cookies, c => c.StartsWith(".AspNetCore.Antiforgery."));
        Assert.Contains("secure", session, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", antiforgery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_server_kept_on_plain_http_on_purpose_can_say_so()
    {
        using var factory = ProductionFactory(allowInsecureCookies: true);
        var cookies = await CookiesSetBySigningUpOverPlainHttpAsync(factory);

        Assert.DoesNotContain(cookies, c => c.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_language_and_theme_cookies_are_secure_in_production_too()
    {
        using var factory = ProductionFactory();
        var client = PlainHttpClient(factory);
        var login = await client.GetAsync("/Account/Login", TestContext.Current.CancellationToken);
        SendBack(client, login.Headers.GetValues("Set-Cookie"));
        var token = Token().Match(await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Groups[1].Value;

        var response = await client.PostAsync("/prefs", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["language"] = "es", ["theme"] = "dark",
        }), TestContext.Current.CancellationToken);

        var set = response.Headers.GetValues("Set-Cookie").Where(c => c.StartsWith("qlang=") || c.StartsWith("qtheme=")).ToList();
        Assert.Equal(2, set.Count);
        Assert.All(set, c => Assert.Contains("secure", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pages_carry_no_inline_script_because_the_policy_would_block_it()
    {
        using var factory = Factory();
        var html = await Client(factory).GetStringAsync("/Account/Login", TestContext.Current.CancellationToken);

        // An inline <script> (the import map was one) is refused by script-src 'self' and only fills the console with errors.
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
    }

    [Fact]
    public async Task Two_simultaneous_first_registrations_make_one_owner()
    {
        using var factory = Factory(allowRegistration: false);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => RegisterAsync(Client(factory), $"owner{i}@example.com")));

        using var scope = factory.Services.CreateScope();
        Assert.Single(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Users.ToList());
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location?.ToString().Contains("Login") != true));
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
    public async Task The_preferences_form_without_a_token_is_refused()
    {
        using var factory = Factory();
        var response = await Client(factory).PostAsync("/prefs", new FormUrlEncodedContent(new Dictionary<string, string> { ["language"] = "es" }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account()
    {
        using var factory = Factory();
        await RegisterAsync(Client(factory), "owner@example.com");
        var attacker = Client(factory);

        async Task<string> TryAsync(string password)
        {
            var token = Token().Match(await attacker.GetStringAsync("/Account/Login", TestContext.Current.CancellationToken)).Groups[1].Value;
            var r = await attacker.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token, ["_handler"] = "login", ["Input.Email"] = "owner@example.com", ["Input.Password"] = password,
            }), TestContext.Current.CancellationToken);
            return r.Headers.Location?.ToString() ?? "";
        }

        for (int i = 0; i < 6; i++) await TryAsync("Wrong-pass1!");
        Assert.Contains("Lockout", await TryAsync("Passw0rd!x"));
    }

    [Fact]
    public async Task Signing_out_with_an_off_site_return_path_still_lands_on_the_site()
    {
        using var factory = Factory();
        var client = Client(factory);
        await RegisterAsync(client, "owner@example.com");
        var token = Token().Match(await client.GetStringAsync("/", TestContext.Current.CancellationToken)).Groups[1].Value;

        var post = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["ReturnUrl"] = "/evil.example",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/", post.Headers.Location!.ToString());
        Assert.DoesNotContain("evil", post.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/manifest.webmanifest")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        using var factory = Factory();
        var response = await Client(factory).GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Contains("form-action 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);   // the Blazor circuit's WebSocket
    }

    [Theory]
    [InlineData("es", "al menos un dígito", "Este campo es obligatorio")]
    [InlineData("en", "at least one digit", "This field is required")]
    public async Task Form_and_password_messages_come_in_the_users_language(string language, string digitRule, string required)
    {
        using var factory = Factory();
        var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", $"qlang={language}");
        var token = Token().Match(await client.GetStringAsync("/Account/Register", TestContext.Current.CancellationToken)).Groups[1].Value;

        var weak = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "register", ["Input.Email"] = "owner@example.com",
            ["Input.Password"] = "Abcdefg!", ["Input.ConfirmPassword"] = "Abcdefg!",
        }), TestContext.Current.CancellationToken);
        Assert.Contains(digitRule, System.Net.WebUtility.HtmlDecode(await weak.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));

        var empty = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "register", ["Input.Email"] = "", ["Input.Password"] = "", ["Input.ConfirmPassword"] = "",
        }), TestContext.Current.CancellationToken);
        Assert.Contains(required, System.Net.WebUtility.HtmlDecode(await empty.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_offline_page_and_the_service_worker_do_not_depend_on_the_fingerprinted_stylesheet()
    {
        using var factory = Factory();
        var client = Client(factory);

        var worker = await client.GetStringAsync("/sw.js", TestContext.Current.CancellationToken);
        var offline = await client.GetStringAsync("/offline.html", TestContext.Current.CancellationToken);

        Assert.DoesNotContain("queueify.css", worker);
        Assert.DoesNotContain("queueify.css", offline);
        Assert.Contains("<style>", offline);
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
