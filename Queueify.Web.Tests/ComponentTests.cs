using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Components.Layout;
using Queueify.Web.Components.Pages;
using Queueify.Web.Components.Shared;
using Queueify.Web.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Tests;

/// <summary>Shared setup: a scratch data folder, a signed-in user, fake catalogs.</summary>
public abstract class WebComponentTests : BunitContext
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
    protected readonly FakeCatalogs Catalogs = new();
    protected UserStores Stores => new(Root);

    protected WebComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddFluentUIComponents();
        Services.AddSingleton(new UserStores(Root));
        Services.AddSingleton<IUserContext>(new FixedUser("alice"));
        Services.AddSingleton<ICatalogs>(Catalogs);
        Services.AddScoped<AlbumSearch>();
        Services.AddScoped<UserQueue>();
        Services.AddScoped<Toasts>();
        Services.AddScoped<AntiforgeryStateProvider, NoAntiforgery>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }

    protected UserQueue Queue => Services.GetRequiredService<UserQueue>();
    protected Toasts Toasts => Services.GetRequiredService<Toasts>();

    protected BacklogItem Seed(string artist, string album, int? year = null, string? source = null, string? tidal = null, string? spotify = null, int daysAgo = 0)
    {
        var q = new UserQueue(Stores, new FixedUser("alice"));
        q.LoadAsync().GetAwaiter().GetResult();
        var item = q.Add(new BacklogItem { Artist = artist, Album = album, Year = year, TidalId = tidal, SpotifyId = spotify }, source ?? "");
        if (daysAgo != 0)
        {
            item.AddedAt = DateTime.Now.AddDays(-daysAgo);
            Stores.QueueOf("alice").Update(item);
        }
        return item;
    }

    protected void SaveKeys(bool spotify = true, bool tidal = true) =>
        Stores.SaveSettings("alice", new AppSettings
        {
            SpotifyClientId = spotify ? "id" : "", SpotifyClientSecret = spotify ? "s" : "",
            TidalClientId = tidal ? "id" : "", TidalClientSecret = tidal ? "s" : "",
        });

    protected static string[] Titles(IRenderedComponent<QueueBrowser> cut) =>
        cut.FindAll(".q-title").Select(e => e.TextContent.Trim()).ToArray();

    protected sealed class FixedUser(string id) : IUserContext
    {
        public Task<string?> UserIdAsync() => Task.FromResult<string?>(id);
    }

    private sealed class NoAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    public sealed class FakeCatalogs : ICatalogs
    {
        public List<AlbumMatch> Spotify { get; set; } = [];
        public List<AlbumMatch> Tidal { get; set; } = [];
        public List<string> Queries { get; } = [];

        /// <summary>When set, searches wait here until the test lets them go.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<List<AlbumMatch>> SearchAsync(MusicService service, string query, string userId, AppSettings settings, CancellationToken ct)
        {
            Queries.Add(query);
            if (Gate != null) await Gate.Task;
            return service == MusicService.Spotify ? Spotify : Tidal;
        }
    }
}

public sealed class QueuePageTests : WebComponentTests
{
    private IRenderedComponent<QueueBrowser> Open(bool listened = false) =>
        Render<QueueBrowser>(p => p.Add(c => c.Listened, listened));

    [Fact]
    public void The_queue_lists_waiting_albums_oldest_first()
    {
        Seed("Soda Stereo", "Signos", 1986, daysAgo: 1);
        Seed("Charly García", "Clics Modernos", 1983, daysAgo: 5);

        Assert.Equal(["Clics Modernos", "Signos"], Titles(Open()));
    }

    [Fact]
    public void The_filter_narrows_the_list_and_says_so_when_nothing_matches()
    {
        Seed("Soda Stereo", "Signos");
        Seed("Charly García", "Clics Modernos");
        var cut = Open();

        cut.Find("#q-filter").Input("garcia");
        Assert.Equal(["Clics Modernos"], Titles(cut));

        cut.Find("#q-filter").Input("zzz");
        Assert.Empty(Titles(cut));
        Assert.Contains(Loc.T("empty.filter.title"), cut.Markup);
    }

    [Fact]
    public void Sorting_changes_the_order()
    {
        Seed("Soda Stereo", "Signos", 1986, daysAgo: 1);
        Seed("Charly García", "Clics Modernos", 1983, daysAgo: 5);
        Seed("Spinetta", "Artaud", 1973, daysAgo: 3);
        var cut = Open();

        cut.Find("#q-sort").Change("year");
        Assert.Equal(["Artaud", "Clics Modernos", "Signos"], Titles(cut));

        cut.Find("#q-sort").Change("newest");
        Assert.Equal(["Signos", "Artaud", "Clics Modernos"], Titles(cut));

        cut.Find("#q-sort").Change("artist");
        Assert.Equal(["Clics Modernos", "Signos", "Artaud"], Titles(cut));
    }

    [Fact]
    public void Marking_listened_moves_the_album_and_undo_brings_it_back()
    {
        Seed("Soda Stereo", "Signos");
        var cut = Open();

        cut.Find(".q-check").Click();

        Assert.Empty(Titles(cut));
        var toast = Toasts.Current!;
        Assert.Contains("Signos", toast.Message);
        Assert.NotNull(toast.Rate);   // the stars to rate it right away

        toast.Action!();
        cut.WaitForAssertion(() => Assert.Equal(["Signos"], Titles(cut)));
    }

    [Fact]
    public void Rating_right_after_listening_is_saved_and_shown_on_the_listened_page()
    {
        Seed("Soda Stereo", "Signos");
        var cut = Open();
        cut.Find(".q-check").Click();

        Toasts.Current!.Rate!(4);

        var listened = Open(listened: true);
        Assert.Equal(["Signos"], Titles(listened));
        Assert.Equal(4, listened.FindAll(".q-star.on").Count);
    }

    [Fact]
    public void Rating_on_the_listened_page_works_with_the_stars()
    {
        var item = Seed("Soda Stereo", "Signos");
        Queue.LoadAsync().GetAwaiter().GetResult();
        Queue.SetListened(Queue.All.Single(), true);
        var cut = Open(listened: true);

        cut.FindAll(".q-star")[2].Click();   // three stars

        Queue.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(3, Queue.All.Single().Rating);
        Assert.Equal(item.Uid, Queue.All.Single().Uid);
    }

    [Fact]
    public void Deleting_removes_the_album_and_undo_restores_it()
    {
        Seed("Soda Stereo", "Signos");
        var cut = Open();

        cut.FindAll(".q-menu-list button").Single(b => b.TextContent.Contains(Loc.T("menu.delete"))).Click();
        Assert.Empty(Titles(cut));

        Toasts.Current!.Action!();
        cut.WaitForAssertion(() => Assert.Equal(["Signos"], Titles(cut)));
    }

    [Fact]
    public void Play_opens_the_preferred_service_with_an_app_link_and_a_web_fallback()
    {
        Stores.SaveSettings("alice", new AppSettings { PreferredService = "Spotify" });
        Seed("Soda Stereo", "Signos", tidal: "111", spotify: "4aawyAB9vmqN3uQ7FjRGTy");
        var cut = Open();

        var play = cut.Find("a.q-btn.primary");
        Assert.Equal("https://open.spotify.com/album/4aawyAB9vmqN3uQ7FjRGTy", play.GetAttribute("href"));
        Assert.Equal("spotify:album:4aawyAB9vmqN3uQ7FjRGTy", play.GetAttribute("data-app"));
        Assert.Contains("noopener", play.GetAttribute("rel"));
    }

    [Fact]
    public void An_album_without_links_has_no_play_button()
    {
        Seed("Soda Stereo", "Signos");
        var cut = Open();
        Assert.Empty(cut.FindAll("a.q-btn.primary"));
        Assert.Contains(Loc.T("item.no_link"), cut.Markup);
    }

    [Fact]
    public void Surprise_me_picks_a_waiting_album_and_an_empty_queue_says_so()
    {
        var empty = Open();
        empty.Find("button[title='" + Loc.T("queue.surprise.tip") + "']").Click();
        Assert.Equal(Loc.T("toast.surprise_empty"), Toasts.Current!.Message);

        Seed("Soda Stereo", "Signos");
        var cut = Open();
        cut.Find("button[title='" + Loc.T("queue.surprise.tip") + "']").Click();

        Assert.Contains("Signos", Toasts.Current!.Message);
        Assert.Single(cut.FindAll(".q-row.picked"));
    }

    [Fact]
    public void The_cover_grid_shows_the_same_albums_as_tiles()
    {
        Seed("Soda Stereo", "Signos");
        var cut = Open();

        cut.Find("fluent-switch").Change(true);

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.q-tile")));
    }

    [Fact]
    public void Texts_follow_the_users_language()
    {
        using var _ = Loc.Override("es");
        var cut = Open();
        Assert.Contains("Para escuchar", cut.Markup);
        Assert.Equal(Loc.T("queue.summary.empty"), cut.Find(".q-sub").TextContent.Trim());
        Assert.NotEqual(Loc.English["queue.summary.empty"], Loc.T("queue.summary.empty"));
    }

    [Fact]
    public void One_star_is_not_called_stars()
    {
        Assert.Equal("1 star", Stars.Label(1));
        Assert.Equal("3 stars", Stars.Label(3));
    }

    [Fact]
    public void Nobody_elses_albums_are_listed()
    {
        var bobs = new UserQueue(Stores, new FixedUser("bob"));
        bobs.LoadAsync().GetAwaiter().GetResult();
        bobs.Add(new BacklogItem { Album = "Bobs album" }, "");

        Assert.DoesNotContain("Bobs album", Open().Markup);
    }
}

public sealed class NavMenuTests : WebComponentTests
{
    [Fact]
    public void Every_page_is_in_the_navigation()
    {
        var cut = Render<NavMenu>();
        var hrefs = cut.FindAll(".q-rail a").Select(a => a.GetAttribute("href")).ToList();

        foreach (var expected in new[] { "", "add", "listened", "photo", "help", "settings", "connect" })
            Assert.Contains(expected, hrefs);
        Assert.Single(cut.FindAll(".q-rail form[action='Account/Logout']"));
    }

    [Fact]
    public void Round_badges_show_how_many_are_waiting_and_listened()
    {
        Seed("A", "One");
        Seed("B", "Two");
        var third = Seed("C", "Three");
        var q = new UserQueue(Stores, new FixedUser("alice"));
        q.LoadAsync().GetAwaiter().GetResult();
        q.SetListened(q.All.Single(i => i.Uid == third.Uid), true);

        var cut = Render<NavMenu>();

        Assert.Equal(["2", "1"], cut.FindAll(".q-rail .q-badge").Select(b => b.TextContent.Trim()).ToArray());
        Assert.Contains(cut.FindAll(".q-rail a").Select(a => a.GetAttribute("aria-label")), l => l!.Contains("2"));
    }

    [Fact]
    public void Badges_follow_changes_without_reloading()
    {
        Seed("A", "One");
        var cut = Render<NavMenu>();
        Assert.Equal("1", cut.Find(".q-rail .q-badge").TextContent.Trim());

        Queue.Add(new BacklogItem { Album = "Two" }, "");

        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find(".q-rail .q-badge").TextContent.Trim()));
    }

    [Fact]
    public void An_empty_queue_has_no_badges() =>
        Assert.Empty(Render<NavMenu>().FindAll(".q-badge"));
}

public sealed class AddPageTests : WebComponentTests
{
    private static AlbumMatch Match(MusicService s, string id, string title, string artist, int year) =>
        new() { Service = s, Id = id, Title = title, Artists = [artist], Year = year };

    private IRenderedComponent<Add> Open() => Render<Add>();

    [Fact]
    public void Without_keys_an_info_bar_points_to_settings_and_adding_by_hand_stays_possible()
    {
        var cut = Open();

        var link = cut.Find(".q-alert a");
        Assert.Equal("settings", link.GetAttribute("href"));
        Assert.NotNull(cut.Find("details.q-quick"));
    }

    [Fact]
    public void With_keys_there_is_no_info_bar()
    {
        SaveKeys();
        Assert.Empty(Open().FindAll(".q-alert"));
    }

    [Fact]
    public void Searching_shows_merged_results_with_service_pills_and_the_score()
    {
        SaveKeys();
        Catalogs.Spotify = [Match(MusicService.Spotify, "sp1", "Clics Modernos", "Charly García", 1983)];
        Catalogs.Tidal = [Match(MusicService.Tidal, "td1", "Clics Modernos", "Charly García", 1983)];
        var cut = Open();

        cut.Find("#q-search").Input("charly garcia clics modernos 1983");
        cut.Find("#q-search").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.q-result")));
        Assert.Empty(cut.FindAll(".q-pill.off"));
        Assert.Contains(Loc.T("result.best"), cut.Markup);
        Assert.Contains(Loc.T("search.press_enter"), cut.Markup);
    }

    [Fact]
    public void A_service_found_on_one_side_only_greys_the_other_pill()
    {
        SaveKeys();
        Catalogs.Tidal = [Match(MusicService.Tidal, "td1", "Signos", "Soda Stereo", 1986)];
        var cut = Open();

        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.StartsWith("Spotify", cut.Find(".q-pill.off").TextContent.Trim()));
    }

    [Fact]
    public void Adding_a_result_saves_it_with_both_links_and_remembers_the_magazine()
    {
        SaveKeys();
        Catalogs.Spotify = [Match(MusicService.Spotify, "sp1", "Signos", "Soda Stereo", 1986)];
        Catalogs.Tidal = [Match(MusicService.Tidal, "td1", "Signos", "Soda Stereo", 1986)];
        var cut = Open();

        cut.Find("#q-magazine").Input("Pelo");
        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.q-result")));
        cut.Find("li.q-result button.q-btn.primary").Click();

        var saved = Stores.QueueOf("alice").LoadAll().Single();
        Assert.Equal(("Soda Stereo", "Signos", 1986, "Pelo", "sp1", "td1"), (saved.Artist, saved.Album, saved.Year, saved.Source, saved.SpotifyId, saved.TidalId));
        Assert.Equal("Pelo", Stores.LoadSettings("alice").LastSource);
        Assert.Empty(cut.FindAll("li.q-result"));
        Assert.Contains("Signos", Toasts.Current!.Message);
    }

    [Fact]
    public void Enter_on_a_confident_result_adds_it_and_undo_removes_it()
    {
        SaveKeys(tidal: false);
        Catalogs.Spotify = [Match(MusicService.Spotify, "sp1", "Signos", "Soda Stereo", 1986)];
        var cut = Open();

        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");   // searches
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("li.q-result")));
        cut.Find("#q-search").KeyDown("Enter");   // adds the highlighted one

        Assert.Single(Stores.QueueOf("alice").LoadAll());
        Toasts.Current!.Action!();
        Assert.Empty(Stores.QueueOf("alice").LoadAll());
    }

    [Fact]
    public void Clearing_the_box_while_a_search_runs_stops_the_spinner_and_drops_the_results()
    {
        SaveKeys(tidal: false);
        Catalogs.Spotify = [Match(MusicService.Spotify, "sp1", "Signos", "Soda Stereo", 1986)];
        Catalogs.Gate = new TaskCompletionSource();
        var cut = Open();

        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");
        cut.WaitForAssertion(() => Assert.Contains(Loc.T("search.searching"), cut.Find(".q-status").TextContent));

        cut.Find("#q-search").Input("so");          // too short: the running search is abandoned
        Catalogs.Gate.SetResult();

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain(Loc.T("search.searching"), cut.Find(".q-status").TextContent);
            Assert.Empty(cut.FindAll("li.q-result"));
        });
    }

    [Fact]
    public void Adding_by_hand_while_a_search_runs_does_not_bring_old_results_back()
    {
        SaveKeys(tidal: false);
        Catalogs.Spotify = [Match(MusicService.Spotify, "sp1", "Signos", "Soda Stereo", 1986)];
        Catalogs.Gate = new TaskCompletionSource();
        var cut = Open();

        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");
        cut.WaitForAssertion(() => Assert.Contains(Loc.T("search.searching"), cut.Find(".q-status").TextContent));
        cut.Find("#qa-album").Input("Artaud");
        cut.Find("form.q-quick-body").Submit();
        Catalogs.Gate.SetResult();

        cut.WaitForAssertion(() => Assert.DoesNotContain(Loc.T("search.searching"), cut.Find(".q-status").TextContent));
        Assert.Empty(cut.FindAll("li.q-result"));
    }

    [Fact]
    public void A_remembered_magazine_is_filled_in_for_the_next_visit()
    {
        Stores.SaveSettings("alice", new AppSettings { LastSource = "Rolling Stone" });
        Assert.Equal("Rolling Stone", Open().Find("#q-magazine").GetAttribute("value"));
    }

    [Fact]
    public void An_album_that_is_already_saved_says_so_and_cannot_be_added_twice()
    {
        SaveKeys(tidal: false);
        Seed("Soda Stereo", "Signos", spotify: "sp1");
        Catalogs.Spotify = [Match(MusicService.Spotify, "sp1", "Signos", "Soda Stereo", 1986)];
        var cut = Open();

        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");

        cut.WaitForAssertion(() => Assert.Contains(Loc.T("search.state.queued"), cut.Markup));
        Assert.Empty(cut.FindAll("li.q-result button.q-btn.primary"));
    }

    [Fact]
    public void Without_a_close_match_adding_without_a_link_is_offered_and_splits_artist_and_album()
    {
        SaveKeys(tidal: false);
        var cut = Open();

        cut.Find("#q-search").Input("Los Abuelos - Himno de mi corazón 1986");
        cut.Find("#q-search").KeyDown("Enter");

        cut.WaitForAssertion(() => cut.Find("button.q-btn.wide").Click());
        var saved = Stores.QueueOf("alice").LoadAll().Single();
        Assert.Equal(("Los Abuelos", "Himno de mi corazón", 1986), (saved.Artist, saved.Album, saved.Year));
        Assert.Null(saved.SpotifyId);
    }

    [Fact]
    public void The_quick_form_adds_an_album_by_hand()
    {
        var cut = Open();

        cut.Find("#qa-artist").Input("Spinetta");
        cut.Find("#qa-album").Input("Artaud");
        cut.Find("#qa-year").Input("1973");
        cut.Find("form.q-quick-body").Submit();

        var saved = Stores.QueueOf("alice").LoadAll().Single();
        Assert.Equal(("Spinetta", "Artaud", 1973), (saved.Artist, saved.Album, saved.Year));
    }

    [Fact]
    public void The_quick_form_needs_an_album_name()
    {
        var cut = Open();
        cut.Find("#qa-artist").Input("Spinetta");
        Assert.NotNull(cut.Find("form.q-quick-body button[type=submit]").GetAttribute("disabled"));
        cut.Find("form.q-quick-body").Submit();
        Assert.Empty(Stores.QueueOf("alice").LoadAll());
    }

    [Fact]
    public void A_failing_service_is_reported_without_losing_the_other()
    {
        SaveKeys();
        Catalogs.Tidal = [Match(MusicService.Tidal, "td1", "Signos", "Soda Stereo", 1986)];
        Services.AddSingleton<ICatalogs>(new ThrowingSpotify(Catalogs));
        var cut = Open();

        cut.Find("#q-search").Input("soda stereo signos");
        cut.Find("#q-search").KeyDown("Enter");

        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll("li.q-result"));
            Assert.Contains("Spotify:", cut.Find(".q-status").TextContent);
        });
    }

    private sealed class ThrowingSpotify(FakeCatalogs inner) : ICatalogs
    {
        public Task<List<AlbumMatch>> SearchAsync(MusicService service, string query, string userId, AppSettings settings, CancellationToken ct) =>
            service == MusicService.Spotify ? throw new CatalogException("Spotify refused") : inner.SearchAsync(service, query, userId, settings, ct);
    }
}
