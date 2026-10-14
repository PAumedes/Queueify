using System.Net;
using System.Security.Claims;
using System.Text;
using Bunit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Components.Pages;
using Queueify.Web.Ocr;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests.Pages;

public sealed class SettingsPageTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qz-" + Guid.NewGuid().ToString("N"));
    private readonly UserStores _stores;
    private readonly PreferencesStore _prefs;
    private HttpStatusCode _token = HttpStatusCode.OK;

    private sealed class FakeApi(Func<HttpStatusCode> token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var isToken = request.RequestUri!.ToString().Contains("token");
            var status = isToken ? token() : HttpStatusCode.OK;
            var body = isToken && status == HttpStatusCode.OK ? """{"access_token":"t","expires_in":3600}""" : """{"albums":{"items":[]}}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    public SettingsPageTests()
    {
        _stores = new UserStores(_root, new EphemeralDataProtectionProvider().CreateProtector("t"));
        _prefs = new PreferencesStore(_stores);
        Services.AddSingleton(_stores);
        Services.AddSingleton(_prefs);
        Services.AddSingleton(new SettingsService(_stores, () => new HttpClient(new FakeApi(() => _token))));
        Services.AddSingleton(new Queueify.Web.Services.DataTransfer(_stores));
        Services.AddSingleton(new OcrOptions());
        var auth = AddAuthorization();
        auth.SetAuthorized("alice@example.test");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "alice"));
        var module = JSInterop.SetupModule("./js/queueify-upload.js");
        module.SetupModule("watch", _ => true).SetupVoid("dispose");
        module.SetupVoid("release", _ => true);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private void Configure(string spotifySecret = "SPOTSECRET", string tidalSecret = "TIDALSECRET") =>
        _stores.SaveSettings("alice", new AppSettings { SpotifyClientId = "spot-id", SpotifyClientSecret = spotifySecret, TidalClientId = "tidal-id", TidalClientSecret = tidalSecret });

    // ── secrets stay on the server ──

    [Fact]
    public void Says_whether_each_service_is_configured_and_never_shows_a_saved_secret()
    {
        Configure();
        var cut = Render<Settings>();

        Assert.DoesNotContain("SPOTSECRET", cut.Markup);
        Assert.DoesNotContain("TIDALSECRET", cut.Markup);
        Assert.Contains(Loc.T("webSettings.keys.on"), cut.Find("[data-service=spotify] .state").TextContent);
        Assert.Equal("spot-id", cut.Find("#spotify-id").GetAttribute("value"));   // an id is not a secret
        Assert.Equal(Loc.T("webSettings.secret.saved"), cut.Find("#spotify-secret").GetAttribute("placeholder"));
    }

    [Fact]
    public void A_service_without_keys_says_so()
    {
        var cut = Render<Settings>();
        Assert.Contains(Loc.T("webSettings.keys.off"), cut.Find("[data-service=tidal] .state").TextContent);
        Assert.Equal(Loc.T("webSettings.secret.empty"), cut.Find("#tidal-secret").GetAttribute("placeholder"));
    }

    [Fact]
    public void The_secret_fields_are_password_fields_that_start_empty_and_are_not_remembered_by_the_browser()
    {
        Configure();
        var field = Render<Settings>().Find("#spotify-secret");

        Assert.Equal("password", field.GetAttribute("type"));
        Assert.Equal("new-password", field.GetAttribute("autocomplete"));
        Assert.True(string.IsNullOrEmpty(field.GetAttribute("value")));
    }

    [Fact]
    public void A_typed_secret_is_saved_and_the_field_is_emptied_afterwards()
    {
        var cut = Render<Settings>();
        cut.Find("#spotify-id").Change("new-id");
        cut.Find("#spotify-secret").Input("brand-new-secret");

        cut.Find("button.save-music").Click();

        var stored = _stores.LoadSettings("alice");
        Assert.Equal(("new-id", "brand-new-secret"), (stored.SpotifyClientId, stored.SpotifyClientSecret));
        Assert.DoesNotContain("brand-new-secret", cut.Markup);
        Assert.Contains(Loc.T("webSettings.saved"), cut.Find("[data-status=music]").TextContent);
        Assert.Contains(Loc.T("webSettings.keys.on"), cut.Find("[data-service=spotify] .state").TextContent);
    }

    [Fact]
    public void Saving_without_touching_anything_says_nothing_changed_and_keeps_the_secrets()
    {
        Configure();
        var cut = Render<Settings>();

        cut.Find("button.save-music").Click();

        Assert.Contains(Loc.T("webSettings.unchanged"), cut.Find("[data-status=music]").TextContent);
        Assert.Equal("SPOTSECRET", _stores.LoadSettings("alice").SpotifyClientSecret);
    }

    [Fact]
    public void Only_the_changed_field_counts_as_an_edit()
    {
        Configure();
        var cut = Render<Settings>();
        _stores.SaveSettings("alice", _stores.LoadSettings("alice").Also(s => s.CountryCode = "ES"));   // a PC synced a new country meanwhile

        cut.Find("#preferred").Change("Spotify");
        cut.Find("button.save-music").Click();

        var stored = _stores.LoadSettings("alice");
        Assert.Equal(("Spotify", "ES"), (stored.PreferredService, stored.CountryCode));
    }

    [Fact]
    public void The_country_list_comes_from_Core_and_shows_the_current_one()
    {
        _stores.SaveSettings("alice", new AppSettings { CountryCode = "ES" });
        var select = Render<Settings>().Find("#country");

        Assert.Equal("ES", select.GetAttribute("value"));
        Assert.Equal(Countries.Codes.Count, select.QuerySelectorAll("option").Length);
    }

    [Fact]
    public void Removing_a_services_keys_asks_nothing_twice_and_leaves_the_other()
    {
        Configure();
        var cut = Render<Settings>();

        cut.Find("button.remove-spotify").Click();

        var stored = _stores.LoadSettings("alice");
        Assert.False(stored.SpotifyConfigured);
        Assert.True(stored.TidalConfigured);
        Assert.Contains(Loc.T("webSettings.keys.off"), cut.Find("[data-service=spotify] .state").TextContent);
    }

    // ── testing the keys ──

    [Fact]
    public async Task The_test_button_reports_that_working_keys_work()
    {
        Configure();
        var cut = Render<Settings>();

        cut.Find("button.test-spotify").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains(Loc.F("webSettings.test.ok", "Spotify"), cut.Find("[data-status=spotify]").TextContent));
    }

    [Fact]
    public async Task The_test_button_passes_on_the_catalogs_actionable_text_without_the_secret()
    {
        Configure(spotifySecret: "SPOTSECRET");
        _token = HttpStatusCode.Unauthorized;
        var cut = Render<Settings>();

        cut.Find("button.test-spotify").Click();

        await cut.WaitForAssertionAsync(() => Assert.Contains(Loc.F("catalog.rejected", "Spotify"), cut.Find("[data-status=spotify]").TextContent));
        Assert.DoesNotContain("SPOTSECRET", cut.Markup);
    }

    [Fact]
    public async Task Testing_with_no_saved_keys_says_to_save_them_first()
    {
        var cut = Render<Settings>();
        cut.Find("button.test-tidal").Click();
        await cut.WaitForAssertionAsync(() => Assert.Contains(Loc.F("webSettings.test.missing", "Tidal"), cut.Find("[data-status=tidal]").TextContent));
    }

    [Fact]
    public void Unsaved_typing_is_not_tested_because_the_test_uses_what_is_saved()
    {
        var cut = Render<Settings>();
        cut.Find("#tidal-id").Change("typed-not-saved");
        cut.Find("#tidal-secret").Input("typed-not-saved");

        cut.Find("button.test-tidal").Click();

        Assert.False(_stores.LoadSettings("alice").TidalConfigured);
    }

    // ── appearance, language, OCR ──

    [Fact]
    public void Theme_language_and_reading_language_are_saved_on_change()
    {
        var cut = Render<Settings>();

        cut.Find("#theme").Change("dark");
        cut.Find("#ocr").Change("es");

        var p = _prefs.Load("alice");
        Assert.Equal(("dark", "system", "es"), (p.Theme, p.Language, p.OcrLanguage));
    }

    [Fact]
    public void Changing_the_language_reloads_so_the_whole_page_changes_language()
    {
        var cut = Render<Settings>();
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>() as Bunit.TestDoubles.BunitNavigationManager;

        cut.Find("#language").Change("es");

        Assert.Equal("es", _prefs.Load("alice").Language);
        Assert.Contains(nav!.History, h => h.Options.ForceLoad);
    }

    [Fact]
    public void An_unknown_theme_posted_by_a_tampered_page_is_ignored()
    {
        var cut = Render<Settings>();
        cut.Find("#theme").Change("<neon>");
        Assert.Equal("system", _prefs.Load("alice").Theme);
    }

    // ── links, sign-out, delete ──

    [Fact]
    public void Links_to_connecting_a_pc_and_to_the_account_page()
    {
        var cut = Render<Settings>();
        Assert.Equal("connect", cut.Find("a.connect").GetAttribute("href"));
        Assert.Equal("Account/Manage", cut.Find("a.account").GetAttribute("href"));
        Assert.Contains(Loc.T("webSettings.signout.text"), cut.Markup);
    }

    [Fact]
    public void Deleting_data_needs_the_box_ticked_and_then_removes_only_this_users_data()
    {
        Configure();
        _stores.QueueOf("alice").Insert(new BacklogItem { Artist = "A", Album = "One" });
        _stores.QueueOf("bob").Insert(new BacklogItem { Artist = "B", Album = "Two" });
        var cut = Render<Settings>();
        Assert.NotNull(cut.Find("button.delete").GetAttribute("disabled"));

        cut.Find("#sure").Change(true);
        cut.Find("button.delete").Click();

        Assert.Empty(_stores.QueueOf("alice").LoadAll());
        Assert.False(_stores.LoadSettings("alice").SpotifyConfigured);
        Assert.Single(_stores.QueueOf("bob").LoadAll());
        Assert.Contains(Loc.T("webSettings.delete.done"), cut.Markup);
    }

    [Fact]
    public void Reads_in_Spanish_when_the_app_is_in_Spanish()
    {
        using var _ = Loc.Override("es");
        var cut = Render<Settings>();
        Assert.Contains("Ajustes", cut.Find("h1").TextContent);
        Assert.Contains(Loc.T("webSettings.delete.title"), cut.Markup);
        Assert.DoesNotContain("Delete my data", cut.Markup);
    }

    [Fact]
    public void Every_control_has_a_label()
    {
        var cut = Render<Settings>();
        foreach (var field in cut.FindAll("input:not([type=file]):not([type=checkbox]), select"))
            Assert.NotEmpty(cut.FindAll($"label[for={field.Id}]"));
        Assert.NotEmpty(cut.FindAll("label[for=sure]"));
    }
}

internal static class SettingsTestExtensions
{
    public static T Also<T>(this T value, Action<T> change) { change(value); return value; }
}
