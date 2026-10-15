using System.Security.Claims;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Services;
using Queueify.Web.Components.Pages;
using Queueify.Web.Components.Shared;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests.Pages;

public sealed class HelpTests : BunitContext
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qh-" + Guid.NewGuid().ToString("N"));
    private readonly PreferencesStore _prefs;
    private readonly TourService _tour = new();

    public HelpTests()
    {
        var stores = new UserStores(_root);
        _prefs = new PreferencesStore(stores);
        Services.AddSingleton(_prefs);
        Services.AddSingleton(_tour);
        var auth = AddAuthorization();
        auth.SetAuthorized("alice@example.test");
        auth.SetClaims(new Claim(ClaimTypes.NameIdentifier, "alice"));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // ── the Help page ──

    [Fact]
    public void Lists_every_shortcut_with_its_keys_and_meaning()
    {
        var cut = Render<Help>();

        var rows = cut.FindAll("table.shortcuts tbody tr");
        Assert.Equal(Shortcuts.Web.Count, rows.Count);
        foreach (var shortcut in Shortcuts.Web)
            Assert.Contains(rows, r => r.TextContent.Contains(shortcut.Keys) && r.TextContent.Contains(shortcut.Description));
        Assert.NotEmpty(cut.FindAll("kbd"));
    }

    [Fact]
    public void Carries_the_quick_guide_and_troubleshooting()
    {
        var text = Render<Help>().Markup;

        foreach (var key in new[] { "help.guide", "help.guide.1", "help.guide.2", "help.guide.3", "help.guide.4", "webHelp.guide.5", "webHelp.sync", "webHelp.trouble.1", "webHelp.trouble.2", "webHelp.trouble.3" })
            Assert.Contains(System.Net.WebUtility.HtmlEncode(Loc.T(key)).Replace("&#39;", "'"), text.Replace("&#39;", "'"));
    }

    [Fact]
    public void Reads_in_Rioplatense_Spanish_when_the_app_is_in_Spanish()
    {
        using var _ = Loc.Override("es");
        var text = Render<Help>().Markup;

        Assert.Contains("Atajos de teclado", text);
        Assert.DoesNotContain("Keyboard shortcuts", text);
    }

    [Fact]
    public void Replay_asks_the_tour_to_start_again()
    {
        var asked = 0;
        _tour.Replayed += () => asked++;

        Render<Help>().Find("button.replay").Click();

        Assert.Equal(1, asked);
    }

    [Fact]
    public void The_help_page_is_open_to_visitors_who_are_not_signed_in()
    {
        var attributes = typeof(Help).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true);
        Assert.Empty(attributes);
    }

    // ── the quick tour ──

    private IRenderedComponent<HelpTour> Tour() => Render<HelpTour>();

    [Fact]
    public void Shows_itself_on_the_first_visit_at_step_one()
    {
        var cut = Tour();

        var dialog = cut.Find("[role=dialog]");
        Assert.Contains(Loc.T("webHelp.tour.1.title"), dialog.TextContent);
        Assert.Contains(Loc.T("webHelp.tour.1"), dialog.TextContent);
        Assert.Contains(Loc.F("tour.counter", Loc.T("webHelp.tour.title"), 1, 4), dialog.TextContent);
    }

    [Fact]
    public void Next_goes_through_every_step_and_done_remembers_it_was_seen()
    {
        var cut = Tour();
        for (var step = 2; step <= 4; step++)
        {
            cut.Find("button.next").Click();
            Assert.Contains(Loc.T($"webHelp.tour.{step}.title"), cut.Find("[role=dialog]").TextContent);
        }
        Assert.Equal(Loc.T("tour.done"), cut.Find("button.next").TextContent.Trim());

        cut.Find("button.next").Click();

        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.True(_prefs.Load("alice").TourSeen);
    }

    [Fact]
    public void Skip_closes_it_and_it_stays_closed_next_time()
    {
        var cut = Tour();
        cut.Find("button.skip").Click();

        Assert.Empty(cut.FindAll("[role=dialog]"));
        Assert.True(_prefs.Load("alice").TourSeen);
        Assert.Empty(Render<HelpTour>().FindAll("[role=dialog]"));
    }

    [Fact]
    public void Escape_closes_it()
    {
        var cut = Tour();
        cut.Find("[role=dialog]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll("[role=dialog]"));
    }

    [Fact]
    public void Replay_brings_it_back_from_step_one_even_after_it_was_seen()
    {
        _prefs.Save("alice", Preferences.Default with { TourSeen = true });
        var cut = Tour();
        Assert.Empty(cut.FindAll("[role=dialog]"));

        cut.InvokeAsync(_tour.Replay);

        Assert.Contains(Loc.T("webHelp.tour.1.title"), cut.Find("[role=dialog]").TextContent);
    }

    [Fact]
    public void Only_one_mounted_tour_shows_so_two_never_overlap()
    {
        var first = Tour();
        var second = Tour();

        Assert.Single(first.FindAll("[role=dialog]"));
        Assert.Empty(second.FindAll("[role=dialog]"));

        first.Dispose();
        _tour.Replay();   // a new owner can take over once the first is gone
        Assert.NotNull(Tour());
    }

    [Fact]
    public void A_visitor_who_is_not_signed_in_gets_no_tour()
    {
        using var anonymous = new BunitContext();
        anonymous.Services.AddSingleton(_prefs);
        anonymous.Services.AddSingleton(new TourService());
        anonymous.AddAuthorization();

        Assert.Empty(anonymous.Render<HelpTour>().FindAll("[role=dialog]"));
    }

    [Fact]
    public void The_dialog_is_named_and_the_buttons_are_real_buttons()
    {
        var cut = Tour();
        Assert.False(string.IsNullOrWhiteSpace(cut.Find("[role=dialog]").GetAttribute("aria-label")));
        Assert.Equal("button", cut.Find("button.next").GetAttribute("type"));
        Assert.Equal(Loc.T("tour.skip"), cut.Find("button.skip").TextContent.Trim());
    }
}
