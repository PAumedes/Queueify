using Queueify.Models;
using Queueify.Services;

namespace Queueify.Tests;

/// <summary>The one-line summaries under each page title.</summary>
public class QueueTextTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 20, 0, 0, DateTimeKind.Local);

    private static BacklogItem Pending(int daysAgo) => new() { Album = "x", AddedAt = Now.AddDays(-daysAgo) };

    private static BacklogItem Listened(double rating = -1) =>
        new() { Album = "x", AddedAt = Now.AddDays(-30), ListenedAt = Now.AddDays(-1), Rating = rating };

    [Theory]
    [InlineData(0, "0 albums")]
    [InlineData(1, "1 album")]
    [InlineData(12, "12 albums")]
    public void Albums_Pluralizes(int count, string expected)
    {
        Assert.Equal(expected, QueueText.Albums(count));
    }

    [Fact]
    public void PendingSummary_WhenEmpty_SaysWhatToDo()
    {
        Assert.Equal("Nothing waiting. Find an album on the left to start.", QueueText.PendingSummary([], Now));
    }

    [Fact]
    public void PendingSummary_CountsAlbumsAndRecentAdditions()
    {
        var items = new[] { Pending(0), Pending(3), Pending(20) };
        Assert.Equal("3 albums waiting  ·  2 added this week", QueueText.PendingSummary(items, Now));
    }

    [Fact]
    public void PendingSummary_LeavesOutTheWeekWhenNothingWasAdded()
    {
        Assert.Equal("1 album waiting", QueueText.PendingSummary([Pending(30)], Now));
    }

    [Fact]
    public void ListenedSummary_WhenEmpty()
    {
        Assert.Equal("Nothing listened yet. Tick the circle next to an album when you've heard it.", QueueText.ListenedSummary([]));
    }

    [Fact]
    public void ListenedSummary_ShowsRatedCountAndAverage()
    {
        var items = new[] { Listened(5), Listened(4), Listened() };
        Assert.Equal("3 albums listened  ·  2 rated, average 4.5 ★", QueueText.ListenedSummary(items));
    }

    [Fact]
    public void ListenedSummary_WithoutRatings()
    {
        Assert.Equal("1 album listened", QueueText.ListenedSummary([Listened()]));
    }
}

public class AppInfoTests
{
    [Fact]
    public void Version_IsMajorMinorPatch()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
    }

    [Fact]
    public void Credits_MentionTheServicesItUses()
    {
        var credits = string.Join("\n", AppInfo.Credits);
        foreach (var name in new[] { "Spotify", "Tidal", "Firebase", "Windows" })
            Assert.Contains(name, credits);
    }
}

public class ShortcutTests
{
    [Fact]
    public void All_ListsTheMainKeyboardShortcuts()
    {
        var keys = Shortcuts.All.Select(s => s.Keys).ToList();
        foreach (var expected in new[] { "Enter", "↑ ↓", "Esc", "Ctrl+K", "Ctrl+F", "Ctrl+B", "Ctrl+V", "F1" })
            Assert.Contains(expected, keys);
        Assert.All(Shortcuts.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
    }
}

public sealed class UiStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "queueify-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void TourSeen_StartsFalse_AndIsRemembered()
    {
        var path = Path.Combine(_dir, "ui.json");
        Assert.False(UiState.Load(path).TourSeen);

        var state = UiState.Load(path);
        state.TourSeen = true;
        state.Save(path);

        Assert.True(UiState.Load(path).TourSeen);
    }

    [Fact]
    public void ResetLayout_RestoresPanelViewAndWindow_ButKeepsPreferences()
    {
        var state = new UiState
        {
            PaneWidth = 700,
            PaneVisible = false,
            CoverView = true,
            Sort = "artist",
            WindowBounds = [10, 10, 800, 600],
            Maximized = true,
            TourSeen = true,
            Theme = "dark",
        };

        state.ResetLayout();

        var fresh = new UiState();
        Assert.Equal(fresh.PaneWidth, state.PaneWidth);
        Assert.True(state.PaneVisible);
        Assert.False(state.CoverView);
        Assert.Equal(fresh.Sort, state.Sort);
        Assert.Null(state.WindowBounds);
        Assert.False(state.Maximized);
        Assert.True(state.TourSeen);
        Assert.Equal("dark", state.Theme);
    }

    [Fact]
    public void Theme_FollowsWindowsUntilChosen_AndIsRemembered()
    {
        var path = Path.Combine(_dir, "ui.json");
        Assert.Equal("system", UiState.Load(path).Theme);

        var state = UiState.Load(path);
        state.Theme = "light";
        state.Save(path);

        Assert.Equal("light", UiState.Load(path).Theme);
    }
}
