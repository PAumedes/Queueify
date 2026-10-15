using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Web.Data;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests;

public class AccountPrefsTests
{
    [Theory]
    [InlineData("es", "en", "en-US", "es")]      // the account's own choice beats the cookie and the browser
    [InlineData("en", "es", "es-AR", "en")]
    [InlineData("system", "es", "en-US", "es")]  // "system" defers to the cookie, then the browser
    [InlineData("system", null, "es-AR", "es")]
    [InlineData(null, null, "fr", "en")]
    public void The_accounts_language_comes_first(string? account, string? cookie, string? accept, string expected) =>
        Assert.Equal(expected, Prefs.Language(account, cookie, accept));

    [Theory]
    [InlineData("dark", "light", "dark")]
    [InlineData("system", "light", "light")]
    [InlineData(null, null, "system")]
    public void The_accounts_theme_comes_first(string? account, string? cookie, string expected) =>
        Assert.Equal(expected, Prefs.Theme(account, cookie));
}

public sealed partial class AccountPrefsAppTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        Directory.CreateDirectory(_root);
        b.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(_root, "app.db")}");
        b.UseSetting("Queueify:DataDir", Path.Combine(_root, "data"));
    });

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static async Task<(HttpClient Client, string Token)> SignedInAsync(WebApplicationFactory<Program> f)
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var page = await client.GetStringAsync("/Account/Register", TestContext.Current.CancellationToken);
        await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value, ["_handler"] = "register", ["Input.Email"] = "me@example.com",
            ["Input.Password"] = "Passw0rd!x", ["Input.ConfirmPassword"] = "Passw0rd!x",
        }), TestContext.Current.CancellationToken);
        var home = await client.GetStringAsync("/", TestContext.Current.CancellationToken);
        return (client, Token().Match(home).Groups[1].Value);
    }

    [Fact]
    public async Task The_language_chosen_in_Settings_applies_without_any_cookie()
    {
        using var factory = Factory();
        var (client, _) = await SignedInAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var id = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("me@example.com"))!.Id;
            scope.ServiceProvider.GetRequiredService<PreferencesStore>().Save(id, Preferences.Default with { Language = "es", Theme = "dark" });
        }

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("<html lang=\"es\"", html);
        Assert.Contains("data-q-theme=\"dark\"", html);
        Assert.Contains("Para escuchar", html);
    }

    [Fact]
    public async Task Deleting_the_account_removes_the_users_folder_too()
    {
        using var factory = Factory();
        var (client, _) = await SignedInAsync(factory);
        string id;
        using (var scope = factory.Services.CreateScope())
        {
            id = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("me@example.com"))!.Id;
            scope.ServiceProvider.GetRequiredService<UserStores>().QueueOf(id).Insert(new Queueify.Models.BacklogItem { Album = "Mine" });
            Assert.True(Directory.Exists(Path.Combine(_root, "data", id)));
        }

        var page = await client.GetStringAsync("/Account/Manage/DeletePersonalData", TestContext.Current.CancellationToken);
        var response = await client.PostAsync("/Account/Manage/DeletePersonalData", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token().Match(page).Groups[1].Value, ["_handler"] = "delete-user", ["Input.Password"] = "Passw0rd!x",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "data", id)));
    }

    [Fact]
    public async Task A_new_account_is_greeted_by_the_quick_tour_once_in_the_shell()
    {
        using var factory = Factory();
        var (client, _) = await SignedInAsync(factory);

        var html = await client.GetStringAsync("/add", TestContext.Current.CancellationToken);

        Assert.Single(Regex.Matches(html, "class=\"tour\""));
    }

    [Fact]
    public async Task The_shell_menu_saves_the_choice_to_the_account_too()
    {
        using var factory = Factory();
        var (client, token) = await SignedInAsync(factory);

        await client.PostAsync("/prefs", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["language"] = "es", ["theme"] = "light", ["returnUrl"] = "/",
        }), TestContext.Current.CancellationToken);

        // A new browser (no cookies) of the same account.
        using var scope = factory.Services.CreateScope();
        var id = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("me@example.com"))!.Id;
        var saved = scope.ServiceProvider.GetRequiredService<PreferencesStore>().Load(id);
        Assert.Equal(("es", "light"), (saved.Language, saved.Theme));
    }
}
