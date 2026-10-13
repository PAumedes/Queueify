using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Queueify.Controls;
using Queueify.Models;
using Queueify.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Queueify;

public sealed partial class MainWindow : Window
{
    private static readonly HttpClient Http = CreateHttpClient();

    private AppSettings _settings;
    private BacklogStore _store;
    private List<BacklogItem> _all;
    private readonly UiState _ui;
    private readonly SpotifyCatalog _spotify;
    private readonly TidalCatalog _tidal;
    private readonly CloudSync _cloud;

    private readonly DispatcherQueueTimer _searchDebounce;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly DispatcherQueueTimer _syncDebounce;
    private CancellationTokenSource? _searchCts;

    private DateTime _lastSyncAttempt;
    private DateTime? _lastSynced;
    private string? _syncError;

    /// <summary>The query the current results belong to; Enter only adds when they match what's typed.</summary>
    private string? _resultsQuery;

    /// <summary>After an album is added, the next click on the photo starts a new search instead of appending.</summary>
    private bool _startFreshFromPhoto = true;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Queueify.ico"));

        _ui = UiState.Load();
        RestoreWindow();
        ApplyTheme();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionButtons();

        _settings = AppData.LoadSettings();
        _store = new BacklogStore(AppData.DatabasePath);
        _all = _store.LoadAll();
        _spotify = new SpotifyCatalog(Http, () => _settings);
        _tidal = new TidalCatalog(Http, () => _settings);
        _cloud = new CloudSync(Http);
        ApplyPreferredService();

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(650);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => _ = SearchAsync();

        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(7);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => Toast.IsOpen = false;

        _syncDebounce = DispatcherQueue.CreateTimer();
        _syncDebounce.Interval = TimeSpan.FromSeconds(1.5);
        _syncDebounce.IsRepeating = false;
        _syncDebounce.Tick += (_, _) => _ = SyncNowAsync();

        Splitter.Target = PaneColumn;
        Splitter.Resized += (_, width) => { _ui.PaneWidth = width; _ui.Save(); };
        SetPaneVisible(_ui.PaneVisible, save: false);
        CoverViewToggle.IsChecked = _ui.CoverView;
        CheckSortItem();

        SourceBox.Text = _settings.LastSource;
        UpdateSetupInfo();
        UpdateSyncButton();
        RefreshQueue();
        ShowResults([]);
        FillHelp();

        // Pick up other PCs' changes when coming back to the window (at most every 20 s).
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated
                && DateTime.UtcNow - _lastSyncAttempt > TimeSpan.FromSeconds(20))
                _ = SyncNowAsync();
        };
        Closed += (_, _) => SaveWindow();
        Root.Loaded += (_, _) =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            _ = SyncNowAsync();
            if (!_ui.TourSeen) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ShowTourStep(0));
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Queueify/1.0");
        return http;
    }

    // ───────────────────────── Window & layout ─────────────────────────

    private void RestoreWindow()
    {
        if (_ui.WindowBounds is [var x, var y, var w, var h] && w > 400 && h > 300
            && DisplayArea.GetFromRect(new RectInt32(x, y, w, h), DisplayAreaFallback.None) != null)
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        }
        else
        {
            SizeToScreen(1440, 920);
        }
        if (_ui.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    private void SaveWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            _ui.Maximized = true;
        }
        else if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _ui.Maximized = false;
            _ui.WindowBounds = [AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height];
        }
        _ui.Save();
    }

    /// <summary>Opens at the given size in DIPs (scaled for the display), capped to 92% of the work area, centered.</summary>
    private void SizeToScreen(int width, int height)
    {
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int w = (int)Math.Min(width * scale, work.Width * 0.92);
        int h = (int)Math.Min(height * scale, work.Height * 0.92);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void SetPaneVisible(bool visible, bool save = true)
    {
        _ui.PaneVisible = visible;
        if (save) _ui.Save();
        if (visible && IsFullWidthPage) GoTo(Page.Queue);
        ApplyPaneLayout();
    }

    /// <summary>Help and Settings use the whole window; the Add albums panel and its rail step aside.</summary>
    private bool IsFullWidthPage => _page is Page.Help or Page.Settings;

    /// <summary>
    /// The Add albums panel follows the user's choice; hidden, it leaves a slim rail in its place.
    /// </summary>
    private void ApplyPaneLayout()
    {
        bool fullWidth = IsFullWidthPage;
        bool open = _ui.PaneVisible && !fullWidth;
        bool rail = !_ui.PaneVisible && !fullWidth;
        PaneColumn.Width = new GridLength(open ? Math.Clamp(_ui.PaneWidth, 320, 760) : rail ? 48 : 0);
        SplitterColumn.Width = new GridLength(open || rail ? 12 : 0);
        AddPane.Visibility = Splitter.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PaneRail.Visibility = rail ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHidePane(object sender, RoutedEventArgs e)
    {
        SetPaneVisible(false);
        // The hide button just disappeared; keep keyboard focus where the panel was.
        PaneRail.Focus(FocusState.Programmatic);
    }

    private void OnShowPane(object sender, RoutedEventArgs e)
    {
        SetPaneVisible(true);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnTogglePaneShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        bool show = !_ui.PaneVisible || IsFullWidthPage;
        SetPaneVisible(show);
        if (show) SearchBox.Focus(FocusState.Programmatic);
        else PaneRail.Focus(FocusState.Programmatic);
    }

    private void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (AddPane.Visibility != Visibility.Visible) SetPaneVisible(true);
        SearchBox.UpdateLayout();
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    private void OnFocusFilter(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_page is not (Page.Queue or Page.Listened)) GoTo(Page.Queue);
        FilterBox.Focus(FocusState.Keyboard);
        FilterBox.SelectAll();
    }

    // ───────────────────────── Search & add ─────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            _searchCts?.Cancel();
            _resultsQuery = null;
            SearchProgress.Visibility = Visibility.Collapsed;
            ShowResults([]);
            return;
        }
        if (query.Length >= 3) _searchDebounce.Start();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                SearchBox.Text = "";
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (_resultsQuery == SearchBox.Text.Trim() && ResultsList.SelectedItem is AlbumResult selected)
                    AddResult(selected);
                else
                    _ = SearchAsync();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (ResultsList.Items.Count == 0) return;
        int index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, ResultsList.Items.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private async Task SearchAsync()
    {
        _searchDebounce.Stop();
        var raw = SearchBox.Text.Trim();
        if (raw.Length == 0) return;
        if (raw == _resultsQuery && SearchProgress.Visibility != Visibility.Visible) return;

        if (!_settings.SpotifyConfigured && !_settings.TidalConfigured)
        {
            _resultsQuery = raw;
            ShowResults([]);
            SearchStatus.Text = "Connect Spotify or Tidal in Settings to search their catalogs.";
            ShowAddWithoutLink(raw);
            return;
        }

        var text = Matcher.SearchText(raw);
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        // While searching, the old results can't be added with Enter (they belong to another query).
        _resultsQuery = null;
        SearchProgress.Visibility = Visibility.Visible;
        SearchStatus.Text = "Searching Spotify and Tidal…";
        AddWithoutLink.Visibility = Visibility.Collapsed;

        var errors = new List<string>();
        var found = new List<AlbumMatch>();
        async Task SearchBothAsync(string q)
        {
            var spotifyTask = _settings.SpotifyConfigured ? Safe(_spotify.SearchAsync(q, cts.Token), cts.Token) : null;
            var tidalTask = _settings.TidalConfigured ? Safe(_tidal.SearchAsync(q, cts.Token), cts.Token) : null;
            foreach (var (service, task) in new[] { ("Spotify", spotifyTask), ("Tidal", tidalTask) })
            {
                if (task == null) continue;
                var (matches, error) = await task;
                if (error != null && !errors.Contains($"{service}: {error}")) errors.Add($"{service}: {error}");
                found.AddRange((matches ?? []).Where(m => !found.Any(f => f.Service == m.Service && f.Id == m.Id)));
            }
        }

        await SearchBothAsync(text);
        if (cts.IsCancellationRequested) return;
        var results = Combine();

        // "Prince 1999": the first search left the year out; the number may be the title, so try with it.
        if (Matcher.ShouldRetryWithYear(raw, results))
        {
            await SearchBothAsync(raw);
            if (cts.IsCancellationRequested) return;
            results = Combine();
        }

        List<AlbumResult> Combine() => Matcher.Combine(
            found.Where(m => m.Service == MusicService.Spotify), found.Where(m => m.Service == MusicService.Tidal), raw);

        SearchProgress.Visibility = Visibility.Collapsed;
        _resultsQuery = raw;
        foreach (var r in results) r.QueueState = QueueStateOf(r);
        ShowResults(results);
        bool confident = results.Count > 0 && results[0].Score >= Matcher.ConfidentScore;

        ResultsList.SelectedIndex = confident ? 0 : -1;
        SearchStatus.Text = string.Join("\n", new[]
        {
            results.Count == 0 ? "Nothing found. Try fewer words, or check the spelling."
                : confident ? (results[0].CanAdd ? "Press Enter to add the highlighted album." : "This album is already saved.")
                : "No close match. Pick one below if it's there.",
        }.Concat(errors));

        if (!confident) ShowAddWithoutLink(raw);
    }

    private static async Task<(List<AlbumMatch>? Matches, string? Error)> Safe(Task<List<AlbumMatch>> search, CancellationToken ct)
    {
        try { return (await search, null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, null); } // replaced by a newer search
        catch (OperationCanceledException) { return (null, "didn't answer in time; try again"); }
        catch (CatalogException ex) { return (null, ex.Message); }
        catch (HttpRequestException ex) { return (null, $"network error ({ex.Message})"); }
        catch (Exception ex) { return (null, $"unexpected response ({ex.Message})"); }
    }

    private void ShowResults(List<AlbumResult> results)
    {
        ResultsList.ItemsSource = results;
        bool any = results.Count > 0;
        ResultsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SearchTips.Visibility = any || SearchBox.Text.Trim().Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!any)
        {
            SearchStatus.Text = "";
            AddWithoutLink.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowAddWithoutLink(string raw)
    {
        AddWithoutLink.Content = $"Add “{raw}” without a link";
        AddWithoutLink.Visibility = Visibility.Visible;
    }

    private string? QueueStateOf(AlbumResult r)
    {
        var titleKey = Matcher.Normalize(r.Title);
        var artistKey = Matcher.Normalize(r.Artist);
        var saved = _all.FirstOrDefault(i =>
            (r.Spotify != null && i.SpotifyId == r.Spotify.Id) ||
            (r.Tidal != null && i.TidalId == r.Tidal.Id) ||
            (Matcher.Normalize(i.Album) == titleKey && Matcher.Normalize(i.Artist) == artistKey));
        return saved == null ? null : saved.IsListened ? "Listened ✓" : "In your queue";
    }

    private void OnAddResult(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumResult r) AddResult(r);
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AlbumResult r) AddResult(r);
    }

    private void AddResult(AlbumResult r)
    {
        if (!r.CanAdd)
        {
            SearchStatus.Text = $"“{r.Title}” is already saved ({r.QueueState}).";
            return;
        }
        AddItem(new BacklogItem
        {
            Artist = r.Artist,
            Album = r.Title,
            Year = r.Year,
            CoverUrl = r.CoverUrl,
            SpotifyId = r.Spotify?.Id,
            TidalId = r.Tidal?.Id,
        });
    }

    private void OnAddWithoutLink(object sender, RoutedEventArgs e)
    {
        var (text, year) = Matcher.ExtractYear(SearchBox.Text.Trim());
        if (text.Length == 0) return;

        // "Artist - Album" is split; anything else is kept whole as the album line.
        var parts = text.Split([" - ", " – ", " — "], 2, StringSplitOptions.TrimEntries);
        AddItem(new BacklogItem
        {
            Artist = parts.Length == 2 ? parts[0] : "",
            Album = parts.Length == 2 ? parts[1] : text,
            Year = year,
        });
    }

    private void AddItem(BacklogItem item)
    {
        var source = SourceBox.Text.Trim();
        item.Source = source.Length > 0 ? source : null;
        item.AddedAt = DateTime.Now;
        _store.Insert(item);
        _all.Add(item);

        if (_settings.LastSource != source)
        {
            _settings.LastSource = source;
            AppData.SaveSettings(_settings);
        }

        SearchBox.Text = "";
        _startFreshFromPhoto = true;
        RefreshQueue();
        ScheduleSync();
        ShowToast($"Added “{item.Album}” to your queue.", undo: () =>
        {
            if (_store.FindByUid(item.Uid) is { Deleted: false } current) _store.Delete(current);
            ReloadQueue();
        });
        SearchBox.Focus(FocusState.Programmatic);
    }

    // ───────────────────────── Queue ─────────────────────────

    private void RefreshQueue()
    {
        // Selection/text events can fire while InitializeComponent is still building the UI.
        if (_all is null) return;

        bool queuePage = _page is Page.Queue or Page.Listened;
        bool listened = _page == Page.Listened;
        var filter = Matcher.Normalize(FilterBox.Text);

        var items = _all
            .Where(i => i.IsListened == listened)
            .Where(i => filter.Length == 0 || Matcher.Normalize($"{i.Artist} {i.Album} {i.Source} {i.Year}").Contains(filter));
        items = _ui.Sort switch
        {
            "newest" => items.OrderByDescending(i => i.AddedAt),
            "artist" => items.OrderBy(i => i.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Year),
            "year" => items.OrderBy(i => i.Year ?? int.MaxValue).ThenBy(i => i.Artist),
            "source" => items.OrderBy(i => i.Source ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.AddedAt),
            _ => listened ? items.OrderByDescending(i => i.ListenedAt) : items.OrderBy(i => i.AddedAt),
        };
        var list = items.ToList();

        QueueList.ItemsSource = _ui.CoverView ? null : list;
        QueueGrid.ItemsSource = _ui.CoverView ? list : null;
        QueueList.Visibility = _ui.CoverView ? Visibility.Collapsed : Visibility.Visible;
        QueueGrid.Visibility = _ui.CoverView ? Visibility.Visible : Visibility.Collapsed;

        // Round count badges in the navigation bar.
        var pending = _all.Where(i => i.IsPending).ToList();
        var heard = _all.Where(i => i.IsListened).ToList();
        SetBadge(QueueNav, QueueBadge, "To listen", pending.Count, "waiting");
        SetBadge(ListenedNav, ListenedBadge, "Listened", heard.Count, "listened");

        (PageIcon.Glyph, PageTitle.Text, PageSummary.Text) = _page switch
        {
            Page.Listened => ("", "Listened", QueueText.ListenedSummary(heard)),
            Page.Photo => ("", "Magazine photo", "Drop a photo of a page, then click the artist and the album on it."),
            Page.Help => ("", "Help", $"{AppInfo.Name} {AppInfo.Version}  ·  how everything works"),
            Page.Settings => ("\uE713", "Settings", "Music services, country, theme and layout. Changes are saved right away."),
            _ => ("", "To listen", QueueText.PendingSummary(pending, DateTime.Now)),
        };

        QueuePanel.Visibility = QueueCommands.Visibility = queuePage ? Visibility.Visible : Visibility.Collapsed;
        PhotoPanel.Visibility = PhotoCommands.Visibility = _page == Page.Photo ? Visibility.Visible : Visibility.Collapsed;
        HelpPanel.Visibility = _page == Page.Help ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = _page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        ApplyPaneLayout();

        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (EmptyIcon.Glyph, EmptyTitle.Text, EmptyText.Text) =
            filter.Length > 0 ? ("", "Nothing matches", "Try a different word, or clear the filter.")
            : listened ? ("", "Nothing listened yet", "Tick the circle next to an album when you've heard it. It moves here so you can rate it.")
            : ("", "Your queue is empty", "Find an album from a magazine on the left, or drop a photo of the page here.");
    }

    private static void SetBadge(NavigationViewItem item, InfoBadge badge, string label, int count, string what)
    {
        badge.Value = count;
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, count > 0 ? $"{label}, {count} {what}" : label);
    }

    private enum Page { Queue, Listened, Photo, Help, Settings }

    private Page _page = Page.Queue;

    /// <summary>Switches page by selecting its navigation item (which raises OnNavSelectionChanged).</summary>
    private void GoTo(Page page) =>
        Nav.SelectedItem = page switch
        {
            Page.Listened => ListenedNav,
            Page.Photo => PhotoNav,
            Page.Help => HelpNav,
            Page.Settings => SettingsNav,
            _ => QueueNav,
        };

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Sync opens a dialog (OnNavItemInvoked); it never becomes the current page, even when
        // selected with the keyboard or accessibility tools.
        if (args.SelectedItemContainer?.Tag is "sync")
        {
            GoTo(_page);
            return;
        }

        var previous = _page;
        _page = (args.SelectedItemContainer?.Tag as string) switch
        {
            "listened" => Page.Listened,
            "photo" => Page.Photo,
            "help" => Page.Help,
            "settings" => Page.Settings,
            _ => Page.Queue,
        };
        if (_page == Page.Settings && previous != Page.Settings) FillSettingsPage();
        RefreshQueue();
        if (_page == Page.Photo && !OcrInfo.IsOpen && !OcrService.SpanishAvailable && !_ui.OcrHintDismissed)
            ShowSpanishOcrWarning(null);
    }

    /// <summary>Sync opens a dialog instead of a page.</summary>
    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is "sync") OnSync(sender, new RoutedEventArgs());
    }

    private void OnHelpShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        GoTo(Page.Help);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => RefreshQueue();

    private void OnSortChosen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string sort) return;
        _ui.Sort = sort;
        _ui.Save();
        CheckSortItem();
        RefreshQueue();
    }

    /// <summary>Ticks the current order in the menu and shows it as the button's label.</summary>
    private void CheckSortItem()
    {
        foreach (var item in new[] { SortOldest, SortNewest, SortArtist, SortYear, SortSource })
        {
            item.IsChecked = (string)item.Tag == _ui.Sort;
            if (item.IsChecked) SortButton.Label = item.Text;
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SortButton, $"Order by: {SortButton.Label}");
    }

    private void OnCoverViewToggled(object sender, RoutedEventArgs e)
    {
        _ui.CoverView = CoverViewToggle.IsChecked == true;
        _ui.Save();
        RefreshQueue();
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await PlayAsync(item);
    }

    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await PlayAsync(item);
    }

    private static Task PlayAsync(BacklogItem item, MusicService? service = null) =>
        (service ?? item.PlayService) switch
        {
            MusicService.Spotify when item.SpotifyId != null => ExternalLinks.OpenSpotifyAsync(item.SpotifyId),
            MusicService.Tidal when item.TidalId != null => ExternalLinks.OpenTidalAsync(item.TidalId),
            _ => Task.CompletedTask,
        };

    private void OnToggleListened(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) SetListened(item, !item.IsListened);
    }

    private void SetListened(BacklogItem item, bool listened)
    {
        var before = item.ListenedAt;
        item.ListenedAt = listened ? DateTime.Now : null;
        _store.Update(item);
        RefreshQueue();
        ScheduleSync();
        ShowToast(listened ? $"Marked “{item.Album}” as listened." : $"Moved “{item.Album}” back to your queue.", undo: () =>
        {
            // A sync may have replaced this album's data meanwhile: undo on the stored version.
            if (_store.FindByUid(item.Uid) is not { Deleted: false } current) return;
            current.ListenedAt = before;
            _store.Update(current);
            ReloadQueue();
        });
    }

    private void Delete(BacklogItem item)
    {
        _store.Delete(item);
        _all.Remove(item);
        RefreshQueue();
        ScheduleSync();
        ShowToast($"Deleted “{item.Album}”.", undo: () =>
        {
            // The stored version may be newer (or already gone after upload, then it's re-created).
            _store.Restore(_store.FindByUid(item.Uid) ?? item);
            ReloadQueue();
        }, severity: InfoBarSeverity.Informational);
    }

    /// <summary>Re-reads the queue from the database (after undo or a sync), redraws and schedules a sync.</summary>
    private void ReloadQueue()
    {
        _all = _store.LoadAll();
        RefreshQueue();
        ScheduleSync();
    }

    /// <summary>Builds the right-click / "…" menu for whichever album it was opened on.</summary>
    private void OnItemMenuOpening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || (menu.Target as FrameworkElement)?.Tag is not BacklogItem item) return;
        menu.Items.Clear();

        MenuFlyoutItem Entry(string text, string glyph, Action action, bool enabled = true)
        {
            var entry = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            return entry;
        }

        menu.Items.Add(Entry("Open in Tidal", "", () => _ = PlayAsync(item, MusicService.Tidal), item.HasTidal));
        menu.Items.Add(Entry("Open in Spotify", "", () => _ = PlayAsync(item, MusicService.Spotify), item.HasSpotify));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(item.IsListened
            ? Entry("Move back to queue", "", () => SetListened(item, false))
            : Entry("Mark as listened", "", () => SetListened(item, true)));
        menu.Items.Add(Entry("Delete", "", () => Delete(item)));
    }

    private void OnRatingChanged(RatingControl sender, object args)
    {
        if (sender.Tag is not BacklogItem item || item.Rating == sender.Value) return;
        item.Rating = sender.Value;
        _store.Update(item);
        ScheduleSync();
    }

    private void OnSurprise(object sender, RoutedEventArgs e)
    {
        var pending = _all.Where(i => i.IsPending).ToList();
        if (pending.Count == 0)
        {
            ShowToast("Your queue is empty. Add some albums first.", severity: InfoBarSeverity.Informational);
            return;
        }
        var pick = pending[Random.Shared.Next(pending.Count)];

        GoTo(Page.Queue);
        FilterBox.Text = "";
        RefreshQueue();
        ListViewBase view = _ui.CoverView ? QueueGrid : QueueList;
        view.SelectedItem = pick;
        view.ScrollIntoView(pick, ScrollIntoViewAlignment.Leading);

        var by = pick.Artist.Length > 0 ? $" by {pick.Artist}" : "";
        ShowToast($"How about “{pick.Album}”{by}?", undo: () => _ = PlayAsync(pick),
            actionText: pick.CanPlay ? $"Play on {pick.PlayLabel}" : null, severity: InfoBarSeverity.Informational);
    }

    private static BacklogItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as BacklogItem;

    /// <summary>Bottom notification with an optional action (usually Undo) that disappears by itself.</summary>
    private void ShowToast(string message, Action? undo = null, string? actionText = "Undo",
        InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        Toast.IsOpen = false;
        Toast.Severity = severity;
        Toast.Message = message;
        Toast.ActionButton = null;
        if (undo != null && actionText != null)
        {
            var button = new Button { Content = actionText };
            button.Click += (_, _) =>
            {
                Toast.IsOpen = false;
                undo();
            };
            Toast.ActionButton = button;
        }
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ───────────────────────── Magazine photo (OCR) ─────────────────────────

    private async void OnOpenPhoto(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".heic", ".tif", ".tiff" })
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        await GuardPhotoAsync(async () =>
        {
            if (await picker.PickSingleFileAsync() is StorageFile file)
                await LoadPhotoFromFileAsync(file);
        });
    }

    /// <summary>
    /// Photo files can be locked, cloud-only or unreadable, and the clipboard can be busy: show the
    /// problem on the photo page instead of letting it crash the app.
    /// </summary>
    private async Task GuardPhotoAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            GoTo(Page.Photo);
            ShowOcrInfo(InfoBarSeverity.Error, $"Couldn't open that image ({ex.Message}). Try saving it as a JPG or PNG file first.");
        }
    }

    private void OnPastePhoto(object sender, RoutedEventArgs e) => _ = GuardPhotoAsync(() => PastePhotoAsync(reportEmpty: true));

    private void OnPasteShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Text boxes keep their normal paste; anywhere else Ctrl+V loads an image.
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or PasswordBox) return;
        args.Handled = true;
        _ = GuardPhotoAsync(() => PastePhotoAsync(reportEmpty: false));
    }

    private async Task PastePhotoAsync(bool reportEmpty)
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Bitmap))
        {
            using var stream = await (await content.GetBitmapAsync()).OpenReadAsync();
            await LoadPhotoAsync(stream);
        }
        else if (content.Contains(StandardDataFormats.StorageItems)
                 && (await content.GetStorageItemsAsync()).OfType<StorageFile>().FirstOrDefault() is { } file)
        {
            await LoadPhotoFromFileAsync(file);
        }
        else if (reportEmpty)
        {
            GoTo(Page.Photo);
            ShowOcrInfo(InfoBarSeverity.Informational, "The clipboard doesn't contain an image.");
        }
    }

    private void OnPhotoDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Read this page";
        }
    }

    private async void OnPhotoDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await GuardPhotoAsync(async () =>
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems)
                    && (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().FirstOrDefault() is { } file)
                {
                    await LoadPhotoFromFileAsync(file);
                }
                else if (e.DataView.Contains(StandardDataFormats.Bitmap))
                {
                    using var stream = await (await e.DataView.GetBitmapAsync()).OpenReadAsync();
                    await LoadPhotoAsync(stream);
                }
            });
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async Task LoadPhotoFromFileAsync(StorageFile file)
    {
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        await LoadPhotoAsync(stream);
    }

    private async Task LoadPhotoAsync(IRandomAccessStream stream)
    {
        GoTo(Page.Photo);
        if (AddPane.Visibility != Visibility.Visible) SetPaneVisible(true);
        ShowOcrInfo(InfoBarSeverity.Informational, "Reading the page…");

        OcrPage page;
        try
        {
            page = await OcrService.RecognizeAsync(stream, _ui.OcrLanguage);
        }
        catch (Exception ex)
        {
            ShowOcrInfo(InfoBarSeverity.Error,
                $"Couldn't read this image ({ex.Message}). iPhone HEIC photos need \"HEIF Image Extensions\" from the Microsoft Store, or send them as JPG.");
            return;
        }

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(page.Bitmap);
        double width = page.Bitmap.PixelWidth, height = page.Bitmap.PixelHeight;
        PhotoImage.Source = source;
        PhotoImage.Width = LineCanvas.Width = width;
        PhotoImage.Height = LineCanvas.Height = height;
        PhotoHint.Visibility = Visibility.Collapsed;

        LineCanvas.Children.Clear();
        var accent = (Color)Application.Current.Resources["SystemAccentColor"];
        var idle = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B));
        var hover = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B));
        var stroke = new SolidColorBrush(accent);
        foreach (var line in page.Lines)
        {
            var box = new Border
            {
                Width = line.Bounds.Width + 8,
                Height = line.Bounds.Height + 6,
                Background = idle,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                Tag = line.Text,
            };
            ToolTipService.SetToolTip(box, line.Text);
            box.PointerEntered += (_, _) => box.Background = hover;
            box.PointerExited += (_, _) => box.Background = idle;
            box.Tapped += OnLineTapped;
            Canvas.SetLeft(box, line.Bounds.X - 4);
            Canvas.SetTop(box, line.Bounds.Y - 3);
            LineCanvas.Children.Add(box);
        }

        _startFreshFromPhoto = true;
        if (page.Lines.Count == 0)
            ShowOcrInfo(InfoBarSeverity.Warning, "No text found. Try a sharper, well-lit photo taken straight on.");
        else if (!page.IsSpanish && !OcrService.SpanishAvailable && !_ui.OcrHintDismissed)
            ShowSpanishOcrWarning(page.LanguageName);
        else
            OcrInfo.IsOpen = false;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FitPhoto);
    }

    private void OnFitPhoto(object sender, RoutedEventArgs e) => FitPhoto();

    private void FitPhoto()
    {
        if (PhotoImage.Width is not > 0) return;
        float zoom = (float)Math.Clamp(PhotoScroller.ViewportWidth / PhotoImage.Width, 0.1, 1.0);
        PhotoScroller.ChangeView(0, 0, zoom, true);
    }

    /// <summary>
    /// A click on a line of the photo puts it in the search box: the first click after an album
    /// was added starts a new search, later clicks are appended (artist, then album).
    /// </summary>
    private void OnLineTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string raw } box) return;
        var (text, year) = OcrService.CleanLine(raw);
        box.Opacity = 0.45;

        var current = SearchBox.Text.Trim();
        var next = _startFreshFromPhoto || current.Length == 0 ? text : $"{current} {text}";
        if (year != null && !next.Contains(year.Value.ToString())) next = $"{next} {year}".Trim();
        _startFreshFromPhoto = false;

        SearchBox.Text = next;
        SearchBox.SelectionStart = next.Length;
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void ShowOcrInfo(InfoBarSeverity severity, string message)
    {
        OcrInfo.ActionButton = null;
        OcrInfo.Content = null;
        OcrInfo.Severity = severity;
        OcrInfo.Message = message;
        OcrInfo.IsOpen = true;
    }

    /// <summary>
    /// The Photo page's offer to install Spanish text recognition. It can be dismissed for good;
    /// Settings → Text recognition still offers it.
    /// </summary>
    private void ShowSpanishOcrWarning(string? usedLanguage)
    {
        var readingAs = usedLanguage != null ? $" (reading as {usedLanguage})" : "";
        ShowPhotoOcrOffer(InfoBarSeverity.Warning,
            $"Spanish text recognition isn't installed on this PC, so accents and ñ may be misread{readingAs}. " +
            "Matching still works. Installing it takes about a minute and Windows will ask for admin permission.",
            OcrOffer.Retry | OcrOffer.Dismiss);
    }

    /// <summary>
    /// The Photo page's message with the actions that make sense next: install (again), restart,
    /// Windows language settings, or "Don't show this again".
    /// </summary>
    private void ShowPhotoOcrOffer(InfoBarSeverity severity, string message, OcrOffer offer)
    {
        ShowOcrInfo(severity, message);
        if (offer.HasFlag(OcrOffer.Retry))
        {
            var install = new Button { Content = "Install Spanish" };
            install.Click += async (_, _) =>
            {
                install.IsEnabled = false;
                install.Content = "Installing…";
                await InstallOcrLanguageAsync("es-ES", ShowPhotoOcrOffer);
            };
            OcrInfo.ActionButton = install;
        }
        else if (offer.HasFlag(OcrOffer.Restart))
        {
            OcrInfo.ActionButton = RestartButton();
        }

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (offer.HasFlag(OcrOffer.WindowsSettings))
        {
            var windows = new HyperlinkButton { Content = "Windows language settings" };
            windows.Click += OnOpenWindowsLanguages;
            links.Children.Add(windows);
        }
        if (offer.HasFlag(OcrOffer.Dismiss) && !_ui.OcrHintDismissed)
        {
            var dismiss = new HyperlinkButton { Content = "Don't show this again" };
            dismiss.Click += (_, _) =>
            {
                _ui.OcrHintDismissed = true;
                _ui.Save();
                OcrInfo.IsOpen = false;
            };
            links.Children.Add(dismiss);
        }
        if (links.Children.Count > 0) OcrInfo.Content = links;
    }

    /// <summary>
    /// Installs a text recognition language and explains every outcome: done, needs a restart,
    /// permission declined or not allowed, window closed, Windows Update error, or another install
    /// still running. <paramref name="report"/> gets what to say and what to offer next.
    /// </summary>
    private async Task InstallOcrLanguageAsync(string tag, Action<InfoBarSeverity, string, OcrOffer> report)
    {
        var name = OcrLanguages.NameOf(tag);
        OcrInstallOutcome outcome;
        try
        {
            var install = OcrService.InstallAsync(tag);
            if (_page == Page.Settings) FillOcrSettings();   // shows it as running
            outcome = await install;
        }
        catch (Exception ex)
        {
            outcome = new OcrInstallOutcome(OcrInstallResult.Failed, ex.Message);
        }

        var offer = OcrService.OfferAfter(outcome.Result);
        switch (outcome.Result)
        {
            case OcrInstallResult.Installed:
                report(InfoBarSeverity.Success, $"{name} text recognition is installed. Open the photo again to re-read it.", offer);
                break;
            case OcrInstallResult.RestartApp:
                report(InfoBarSeverity.Success, $"{name} text recognition is installed. Restart Queueify to start using it.", offer);
                break;
            case OcrInstallResult.RestartWindows:
                report(InfoBarSeverity.Warning, $"{name} text recognition is installed, but Windows needs a restart to finish.", offer);
                break;
            case OcrInstallResult.Cancelled:
                report(InfoBarSeverity.Informational,
                    "Nothing was installed: Windows' permission prompt was declined. Installing needs an administrator " +
                    "(on a family or work PC, ask the person who manages it). You can try again any time.", offer);
                break;
            case OcrInstallResult.Interrupted:
                report(InfoBarSeverity.Warning,
                    "The install window was closed before it finished. Try again and leave it open; it closes by itself.", offer);
                break;
            case OcrInstallResult.Busy:
                report(InfoBarSeverity.Informational, "Another install is still running. Wait until its window closes.", offer);
                break;
            case OcrInstallResult.Blocked:
                report(InfoBarSeverity.Warning,
                    $"This PC doesn't let Queueify ask for administrator permission ({outcome.Error}). " +
                    "Add the language in Windows language settings, or ask the person who manages this PC.", offer);
                break;
            default:
                var detail = outcome.Error != null ? $" Windows said: {outcome.Error}" : "";
                report(InfoBarSeverity.Error,
                    $"Windows couldn't install {name} text recognition.{detail} It downloads from Windows Update, so check " +
                    "the internet connection and try again. On a PC whose updates are managed by an organization, its " +
                    "administrator has to allow optional features to download from Windows Update.", offer);
                break;
        }
        if (_page == Page.Settings) FillOcrSettings();
    }

    private Button RestartButton()
    {
        var restart = new Button { Content = "Restart Queueify" };
        restart.Click += (_, _) => RestartApp();
        return restart;
    }

    /// <summary>Starts a fresh copy and closes this one (which saves the window and layout as usual).</summary>
    private void RestartApp()
    {
        // Saved first, so the new copy opens where this one was.
        SaveWindow();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowToast($"Couldn't restart ({ex.Message}). Close Queueify and open it again.", severity: InfoBarSeverity.Warning);
            return;
        }
        Close();
    }

    // ───────────────────────── Sync between PCs ─────────────────────────

    private void ScheduleSync()
    {
        if (!_settings.CloudConfigured) return;
        _syncDebounce.Stop();
        _syncDebounce.Start();
    }

    private Task<string?>? _syncTask;
    private Task<string?>? _followUpSync;

    /// <summary>
    /// Downloads other PCs' changes and uploads this PC's. Returns an error message, or null when it
    /// worked (or sync isn't set up). If a sync is already running, one more runs right after it and
    /// the caller gets that result, so "Sync now" never reports success for a sync that didn't happen.
    /// Failures are harmless: pending changes stay and go up next time.
    /// </summary>
    private Task<string?> SyncNowAsync()
    {
        if (!_settings.CloudConfigured) return Task.FromResult<string?>(null);
        if (_syncTask is { IsCompleted: false } running)
            return _followUpSync ??= AfterAsync(running);
        return _syncTask = RunSyncAsync();

        async Task<string?> AfterAsync(Task previous)
        {
            await previous;
            _followUpSync = null;
            return await SyncNowAsync();
        }
    }

    private async Task<string?> RunSyncAsync()
    {
        _lastSyncAttempt = DateTime.UtcNow;
        SyncNav.Content = "Syncing…";

        // Sync a copy: the UI thread keeps using _settings while the sync runs in the background.
        var work = _settings.Clone();
        var sharedStampBefore = _settings.SharedSettingsUpdatedAt;
        string? error = null;
        try
        {
            var result = await Task.Run(() => _cloud.SyncAsync(_store, work, CancellationToken.None));
            if (_settings.CloudQueueKey == work.CloudQueueKey)   // still connected to the same queue
            {
                _settings.CloudLastServerTime = work.CloudLastServerTime;
                // Take downloaded keys/preferences unless the user saved Settings while this sync ran.
                bool sharedChanged = _settings.SharedSettingsUpdatedAt == sharedStampBefore
                                     && work.SharedSettingsUpdatedAt != sharedStampBefore;
                if (sharedChanged)
                {
                    var keysBefore = (KeysOf(MusicService.Tidal), KeysOf(MusicService.Spotify));
                    _settings.CopySharedFrom(work);
                    _settings.Normalize();
                    if (KeysOf(MusicService.Tidal) != keysBefore.Item1) ForgetTestResult(MusicService.Tidal);
                    if (KeysOf(MusicService.Spotify) != keysBefore.Item2) ForgetTestResult(MusicService.Spotify);
                }
                AppData.SaveSettings(_settings);

                if (result.SettingsChanged && sharedChanged)
                {
                    ApplyPreferredService();
                    UpdateSetupInfo();
                    _resultsQuery = null;
                    if (_page == Page.Settings) FillSettingsPage(keepTypedKeys: true);
                }
                if (result.Downloaded > 0 || result.SettingsChanged)
                {
                    _all = _store.LoadAll();
                    RefreshQueue();
                }
                _lastSynced = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            error = DescribeSyncError(ex);
        }

        _syncError = _settings.CloudConfigured ? error : null;
        UpdateSyncButton();
        return error;
    }

    private static string DescribeSyncError(Exception ex) => ex switch
    {
        CatalogException => ex.Message,
        HttpRequestException => "Can't reach Firebase right now. Your changes are kept and will sync when you're online.",
        TaskCanceledException => "Firebase didn't answer in time. Your changes will sync later.",
        IOException or UnauthorizedAccessException => $"Couldn't save on this PC ({ex.Message}). Your changes will sync later.",
        _ => $"Sync failed ({ex.Message}). Your changes are kept and will sync later.",
    };

    /// <summary>The Sync icon in the navigation bar shows the state; its tooltip says why.</summary>
    private void UpdateSyncButton()
    {
        string label, glyph, tip;
        if (!_settings.CloudConfigured)
            (label, glyph, tip) = ("Sync", "", "Sync: keep the same queue on all your PCs");
        else if (_syncError != null)
            (label, glyph, tip) = ("Not synced", "", $"Not synced: {_syncError}");
        else
            (label, glyph, tip) = ("Synced", "",
                _lastSynced is DateTime t ? $"Synced with Firebase at {t:t}" : "Synced with Firebase");

        SyncNav.Content = label;
        SyncIcon.Glyph = SettingsSyncIcon.Glyph = glyph;
        SettingsSyncStatus.Text = tip;
        ToolTipService.SetToolTip(SyncNav, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SyncNav, tip);
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        if (_settings.CloudConfigured) await ShowSyncConnectedAsync();
        else await ShowSyncSetupAsync();
    }

    /// <summary>First-time setup: create the online queue (first PC) or join it with a sync code.</summary>
    private async Task ShowSyncSetupAsync()
    {
        var create = new RadioButton { Content = "This is my first PC: create the online queue", GroupName = "sync", IsChecked = true };
        var join = new RadioButton { Content = "I already set it up on another PC", GroupName = "sync" };

        var projectBox = new TextBox { Header = "Firebase project ID", PlaceholderText = "e.g. queueify-1a2b3" };
        var copyRules = new Button { Content = "Copy security rules" };
        copyRules.Click += (_, _) =>
        {
            CopyToClipboard(CloudSync.SecurityRules);
            copyRules.Content = "Rules copied ✓";
        };
        var createPanel = new StackPanel { Spacing = 10 };
        createPanel.Children.Add(Paragraph(
            "1. Open the Firebase console and sign in with your Google account.\n" +
            "2. Create a project with any name (e.g. Queueify). Google Analytics isn't needed.\n" +
            "3. In the menu, open Build → Firestore Database → Create database. If asked, choose the Standard edition, " +
            "keep the database ID \"(default)\", pick a location near you (e.g. southamerica-east1) and start in production mode.\n" +
            "4. Open the Rules tab, replace everything with Queueify's rules (button below) and click Publish.\n" +
            "5. Copy the project ID (it's in the address bar after /project/, or in Project settings) and paste it here."));
        createPanel.Children.Add(new HyperlinkButton { Content = "Open the Firebase console", NavigateUri = new Uri("https://console.firebase.google.com/") });
        createPanel.Children.Add(copyRules);
        createPanel.Children.Add(projectBox);

        var codeBox = new TextBox { Header = "Sync code", PlaceholderText = "queueify:…" };
        var joinPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        joinPanel.Children.Add(Paragraph(
            "On the PC that's already synced, click Sync and copy the sync code, then paste it here. " +
            "(Even easier: copy that PC's whole Queueify folder to this one. It's already connected.)"));
        joinPanel.Children.Add(codeBox);

        create.Checked += (_, _) => { createPanel.Visibility = Visibility.Visible; joinPanel.Visibility = Visibility.Collapsed; };
        join.Checked += (_, _) => { createPanel.Visibility = Visibility.Collapsed; joinPanel.Visibility = Visibility.Visible; };

        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };

        var panel = new StackPanel { Spacing = 12, Width = 480 };
        panel.Children.Add(Paragraph("Keep one queue on all your PCs. It's stored in your own Google Firebase account, on the free plan (no credit card)."));
        panel.Children.Add(create);
        panel.Children.Add(join);
        panel.Children.Add(createPanel);
        panel.Children.Add(joinPanel);
        panel.Children.Add(progress);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "Sync between your PCs",
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        bool joined = false;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                string project, key;
                joined = join.IsChecked == true;
                if (joined)
                {
                    if (!CloudSync.TryParseSyncCode(codeBox.Text, out project, out key))
                    {
                        Fail("That doesn't look like a sync code. It starts with \"queueify:\".");
                        return;
                    }
                }
                else
                {
                    if (CloudSync.NormalizeProjectId(projectBox.Text) is not string id)
                    {
                        Fail("Paste the Firebase project ID, e.g. queueify-1a2b3.");
                        return;
                    }
                    project = id;
                    key = CloudSync.NewQueueKey();
                }

                // Try the connection on a copy; the live settings only change if the first sync works.
                var trial = _settings.Clone();
                trial.CloudProjectId = project;
                trial.CloudQueueKey = key;
                trial.CloudLastServerTime = null;
                // Creating: this PC's keys go up. Joining: the online queue's keys win (or this PC's,
                // if the queue has none yet).
                trial.SharedSettingsUpdatedAt = joined ? null
                    : trial.SpotifyConfigured || trial.TidalConfigured ? BacklogStore.Now() : null;

                progress.Visibility = Visibility.Visible;
                error.IsOpen = false;
                d.IsPrimaryButtonEnabled = false;
                string? problem = null;
                try
                {
                    // A sync for the previous queue may still be finishing; let it end first, or its
                    // "uploaded" marks could undo the next line.
                    if (_syncTask is { } previous) await previous;
                    // Albums synced to an earlier queue are marked clean; this queue needs all of them.
                    _store.MarkAllDirty();
                    await Task.Run(() => _cloud.SyncAsync(_store, trial, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    problem = DescribeSyncError(ex);
                }
                progress.Visibility = Visibility.Collapsed;
                d.IsPrimaryButtonEnabled = true;

                if (problem != null)
                {
                    Fail(problem);
                    return;
                }

                _settings = trial;
                AppData.SaveSettings(_settings);
                ApplyPreferredService();
                UpdateSetupInfo();
                _all = _store.LoadAll();
                RefreshQueue();
                _syncError = null;
                _lastSynced = DateTime.Now;
                UpdateSyncButton();
            }
            catch (Exception ex)
            {
                Fail(DescribeSyncError(ex));
            }
            finally
            {
                deferral.Complete();
            }

            void Fail(string message)
            {
                error.Message = message;
                error.IsOpen = true;
                args.Cancel = true;
            }
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || !_settings.CloudConfigured) return;

        if (joined)
            ShowToast($"Connected. Your queue now has {_all.Count} albums from all your PCs.");
        else
            await ShowSyncConnectedAsync(justCreated: true);
    }

    /// <summary>Shows the sync code for other PCs, plus "Sync now" and "Disconnect".</summary>
    private async Task ShowSyncConnectedAsync(bool justCreated = false)
    {
        var code = CloudSync.SyncCode(_settings);
        var codeBox = new TextBox { Header = "Sync code", Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var copy = new Button { Content = "Copy sync code" };
        copy.Click += (_, _) =>
        {
            CopyToClipboard(code);
            copy.Content = "Copied ✓";
        };

        var status = _syncError ?? (_lastSynced is DateTime t ? $"Last synced at {t:t}." : "Synced.");
        var panel = new StackPanel { Spacing = 12, Width = 480 };
        panel.Children.Add(Paragraph(justCreated
            ? "Done! Your queue is online now."
            : $"This PC is synced with your Firebase project \"{_settings.CloudProjectId}\". {status}"));
        panel.Children.Add(Paragraph(
            "To add another PC, copy this whole Queueify folder to it: it's already connected. " +
            "Or open Sync on the other PC, choose \"I already set it up on another PC\" and paste this code:"));
        panel.Children.Add(codeBox);
        panel.Children.Add(copy);
        panel.Children.Add(Paragraph("Keep the code private: anyone who has it can see and change your queue."));

        var dialog = new ContentDialog
        {
            Title = "Sync between your PCs",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "Sync now",
            SecondaryButtonText = "Disconnect this PC",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };

        switch (await ShowDialogAsync(dialog))
        {
            case ContentDialogResult.Primary:
                var problem = await SyncNowAsync();
                ShowToast(problem ?? "Synced.", severity: problem == null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                break;
            case ContentDialogResult.Secondary:
                _settings.CloudProjectId = _settings.CloudQueueKey = "";
                _settings.CloudLastServerTime = null;
                _syncError = null;
                AppData.SaveSettings(_settings);
                UpdateSyncButton();
                ShowToast("This PC no longer syncs. Its queue stays here; the online copy is untouched.",
                    severity: InfoBarSeverity.Informational);
                break;
        }
    }

    private bool _dialogOpen;

    /// <summary>
    /// WinUI allows one ContentDialog at a time and throws on a second; a quick double-click on Sync
    /// or Settings would otherwise crash the app. A second request while one is open is ignored.
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        // Dialogs live outside the window's element tree, so they don't inherit the chosen theme.
        dialog.RequestedTheme = Root.ActualTheme;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private static void CopyToClipboard(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ───────────────────────── Help & quick tour ─────────────────────────

    private void FillHelp()
    {
        ShortcutList.ItemsSource = Shortcuts.All;
        AboutVersion.Text = $"Version {AppInfo.Version}";
        AboutTagline.Text = AppInfo.Tagline;
        AboutCredits.ItemsSource = AppInfo.Credits;
        AboutDataPath.Text = AppData.Folder;
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppData.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppData.Folder) { UseShellExecute = true });
    }

    private int _tourStep;

    /// <summary>The four stops of the quick tour: what to point at and what to say.</summary>
    private (FrameworkElement Target, string Title, string Text)[] TourSteps =>
    [
        (SearchBox, "Find an album",
            "Type what the magazine says, like “Charly García Clics Modernos”. Press Enter to search Spotify and Tidal, and Enter again to add the best match."),
        (QueueNav, "Your queue",
            "Everything you add waits here. Press Play to open it in Tidal or Spotify. Tick the circle when you've heard it; it moves to Listened, where you can rate it."),
        (PhotoNav, "Too lazy to type?",
            "Photograph a magazine page with your phone and drop the photo into this window. Then click the artist and the album on it."),
        (SyncNav, "All your PCs",
            "Connect Sync once to keep the same queue on every PC. Help (F1) explains everything else."),
    ];

    private void ShowTourStep(int step)
    {
        var steps = TourSteps;
        if (step >= steps.Length)
        {
            EndTour();
            return;
        }
        _tourStep = step;
        if (step == 0)
        {
            // Seen as soon as it starts, so however it ends (even closing the app) it won't nag again.
            _ui.TourSeen = true;
            _ui.Save();
            GoTo(Page.Queue);
            if (!_ui.PaneVisible) SetPaneVisible(true);
        }

        var (target, title, text) = steps[step];
        Tour.IsOpen = false;
        Tour.Target = target;
        Tour.Title = $"{title}  ·  {step + 1} of {steps.Length}";
        Tour.Subtitle = text;
        Tour.ActionButtonContent = step == steps.Length - 1 ? "Done" : "Next";
        Tour.IsOpen = true;
    }

    private void OnTourNext(TeachingTip sender, object args) => ShowTourStep(_tourStep + 1);

    private void OnTourClosed(TeachingTip sender, object args) => EndTour();

    private void EndTour()
    {
        Tour.IsOpen = false;
        _ui.TourSeen = true;
        _ui.Save();
    }

    private void OnReplayTour(object sender, RoutedEventArgs e) => ShowTourStep(0);

    // ───────────────────────── Settings ─────────────────────────

    private void UpdateSetupInfo() =>
        SetupInfo.IsOpen = !_settings.SpotifyConfigured && !_settings.TidalConfigured;

    private void ApplyPreferredService() =>
        BacklogItem.Preferred = Enum.TryParse<MusicService>(_settings.PreferredService, out var s) ? s : MusicService.Tidal;

    /// <summary>"Open settings" in the Add albums panel.</summary>
    private void OnSettings(object sender, RoutedEventArgs e) => GoTo(Page.Settings);

    /// <summary>
    /// The values the Settings page shows. Edits are compared against it (AppSettings.ApplyEdits),
    /// so a value a sync brought in while the page was open isn't overwritten by an untouched box.
    /// </summary>
    private AppSettings _settingsShown = new();

    /// <summary>Set while the page fills its controls, so their change events don't save anything.</summary>
    private bool _fillingSettings;

    /// <summary>
    /// The last connection test per service failed with this message; KeysRejected tells a wrong key
    /// apart from a network problem. Cleared when the keys change (here or from another PC).
    /// </summary>
    private readonly Dictionary<MusicService, (string Message, bool KeysRejected)> _serviceProblem = [];

    /// <summary>Services whose keys are being tested right now.</summary>
    private readonly HashSet<MusicService> _testing = [];

    /// <summary>Services whose current keys passed a test search in this session.</summary>
    private readonly HashSet<MusicService> _verified = [];

    /// <summary>The keys changed (saved, removed, or synced from another PC): earlier test results no longer apply.</summary>
    private void ForgetTestResult(MusicService service)
    {
        _serviceProblem.Remove(service);
        _verified.Remove(service);
        (service == MusicService.Tidal ? TidalCard : SpotifyCard).HideResult();
    }

    /// <summary>
    /// Fills the page from the current settings. After a sync, keys the user is typing (boxes that
    /// differ from what was shown) are kept, and so is what they were compared against.
    /// </summary>
    private void FillSettingsPage(bool keepTypedKeys = false)
    {
        _fillingSettings = true;
        try
        {
            // Shown in standard form ("ar" as Argentina), and compared in that form, so picking
            // nothing never counts as a change.
            var shown = _settings.ForSettingsPage();
            if (keepTypedKeys && (TidalCard.ClientId != _settingsShown.TidalClientId || TidalCard.ClientSecret != _settingsShown.TidalClientSecret))
            {
                shown.TidalClientId = _settingsShown.TidalClientId;
                shown.TidalClientSecret = _settingsShown.TidalClientSecret;
            }
            else
            {
                TidalCard.ClientId = shown.TidalClientId;
                TidalCard.ClientSecret = shown.TidalClientSecret;
            }
            if (keepTypedKeys && (SpotifyCard.ClientId != _settingsShown.SpotifyClientId || SpotifyCard.ClientSecret != _settingsShown.SpotifyClientSecret))
            {
                shown.SpotifyClientId = _settingsShown.SpotifyClientId;
                shown.SpotifyClientSecret = _settingsShown.SpotifyClientSecret;
            }
            else
            {
                SpotifyCard.ClientId = shown.SpotifyClientId;
                SpotifyCard.ClientSecret = shown.SpotifyClientSecret;
            }
            _settingsShown = shown;

            if (!keepTypedKeys)
            {
                TidalCard.IsExpanded = !_settings.TidalConfigured;
                SpotifyCard.IsExpanded = !_settings.SpotifyConfigured;
            }

            PreferredBox.SelectedItem = shown.PreferredService;
            var countries = Countries.Options(shown.CountryCode, CultureInfo.CurrentCulture);
            CountryBox.ItemsSource = countries;
            CountryBox.SelectedItem = countries.FirstOrDefault(c => c.Code == shown.CountryCode);
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _ui.Theme)
                                    ?? ThemeBox.Items[0];
            SettingsDataPath.Text = AppData.Folder;
            FillOcrSettings();
            UpdateServiceCards();
        }
        finally
        {
            _fillingSettings = false;
        }
    }

    private void UpdateServiceCards()
    {
        Show(TidalCard, MusicService.Tidal, _settings.TidalConfigured);
        Show(SpotifyCard, MusicService.Spotify, _settings.SpotifyConfigured);

        void Show(ServiceCard card, MusicService service, bool configured)
        {
            if (!configured)
                card.ShowState(false, false, "Not connected", $"Not connected. Add your free developer keys to search {service}.");
            else if (_testing.Contains(service))
                card.ShowState(false, true, "Testing", "Testing the keys with a search…");
            else if (_serviceProblem.TryGetValue(service, out var problem))
                card.ShowState(false, true, problem.KeysRejected ? "Check keys" : "Not reachable", problem.Message);
            else if (_verified.Contains(service))
                card.ShowState(true, true, "Connected", $"Searches include {service}. The keys passed the test.");
            else
                card.ShowState(true, true, "Keys saved", $"Searches include {service}. “Save and test” checks the keys.");
        }
    }

    /// <summary>
    /// Applies what changed between <see cref="_settingsShown"/> and <paramref name="edited"/>, saves,
    /// and lets the rest of the app catch up. Only real changes are stamped as the newest shared
    /// settings, so saving unchanged values never pushes older keys to the other PCs.
    /// </summary>
    private void CommitSettingsEdit(AppSettings edited)
    {
        if (_settings.ApplyEdits(_settingsShown, edited))
        {
            _settings.SharedSettingsUpdatedAt = BacklogStore.Now();
            ScheduleSync();
        }
        _settingsShown = edited;
        AppData.SaveSettings(_settings);
        ApplyPreferredService();
        UpdateSetupInfo();
        UpdateServiceCards();
        _resultsQuery = null;
        RefreshQueue();
    }

    private async void OnSaveServiceKeys(object? sender, EventArgs e)
    {
        if (sender is not ServiceCard card) return;
        var service = card == TidalCard ? MusicService.Tidal : MusicService.Spotify;
        if (card.ClientId.Length == 0 || card.ClientSecret.Length == 0)
        {
            card.ShowResult(InfoBarSeverity.Warning, "Paste both the Client ID and the Client secret.");
            return;
        }

        var edited = _settingsShown.Clone();
        if (service == MusicService.Tidal)
            (edited.TidalClientId, edited.TidalClientSecret) = (card.ClientId, card.ClientSecret);
        else
            (edited.SpotifyClientId, edited.SpotifyClientSecret) = (card.ClientId, card.ClientSecret);
        ForgetTestResult(service);
        _testing.Add(service);
        CommitSettingsEdit(edited);
        await TestServiceAsync(card, service);
    }

    private string KeysOf(MusicService service) => service == MusicService.Tidal
        ? $"{_settings.TidalClientId}:{_settings.TidalClientSecret}"
        : $"{_settings.SpotifyClientId}:{_settings.SpotifyClientSecret}";

    /// <summary>
    /// A real search with the saved keys: the quickest proof that they work. The result only counts
    /// if the keys are still the ones tested (they may be removed or synced meanwhile).
    /// </summary>
    private async Task TestServiceAsync(ServiceCard card, MusicService service)
    {
        var tested = KeysOf(service);
        _testing.Add(service);
        card.IsBusy = true;
        card.HideResult();
        UpdateServiceCards();
        (string Message, bool KeysRejected)? problem = null;
        int found = 0;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var search = service == MusicService.Tidal
                ? _tidal.SearchAsync("Charly Garcia Clics Modernos", cts.Token)
                : _spotify.SearchAsync("Charly Garcia Clics Modernos", cts.Token);
            found = (await search).Count;
        }
        catch (CatalogException ex) { problem = (ex.Message, ex.KeysRejected); }
        catch (OperationCanceledException) { problem = ($"{service} didn't answer in time. Try again in a moment.", false); }
        catch (HttpRequestException ex) { problem = ($"Couldn't reach {service} ({ex.Message}). Check the internet connection.", false); }
        catch (Exception ex) { problem = ($"Unexpected answer from {service} ({ex.Message}).", false); }
        finally
        {
            _testing.Remove(service);
            card.IsBusy = false;
        }

        if (KeysOf(service) != tested)
        {
            UpdateServiceCards();
            return;
        }
        if (problem is { } p) _serviceProblem[service] = p;
        else _verified.Add(service);
        UpdateServiceCards();
        if (problem is { } shown)
            card.ShowResult(InfoBarSeverity.Error, shown.Message);
        else
            card.ShowResult(InfoBarSeverity.Success, $"Connected. A test search found {QueueText.Albums(found)}.");
    }

    private void OnRemoveServiceKeys(object? sender, EventArgs e)
    {
        if (sender is not ServiceCard card) return;
        var service = card == TidalCard ? MusicService.Tidal : MusicService.Spotify;
        var (oldId, oldSecret) = service == MusicService.Tidal
            ? (_settings.TidalClientId, _settings.TidalClientSecret)
            : (_settings.SpotifyClientId, _settings.SpotifyClientSecret);

        SetKeys(service, "", "");
        ShowToast($"Removed the {service} keys.", undo: () =>
        {
            // Keys saved here or synced from another PC since then are newer: undo must not replace them.
            if (KeysOf(service) != ":")
            {
                ShowToast($"Not undone: other {service} keys were saved since.", severity: InfoBarSeverity.Warning);
                return;
            }
            SetKeys(service, oldId, oldSecret);
        }, severity: InfoBarSeverity.Informational);

        void SetKeys(MusicService s, string id, string secret)
        {
            var edited = _settingsShown.Clone();
            if (s == MusicService.Tidal) (edited.TidalClientId, edited.TidalClientSecret) = (id, secret);
            else (edited.SpotifyClientId, edited.SpotifyClientSecret) = (id, secret);
            ForgetTestResult(s);
            var target = s == MusicService.Tidal ? TidalCard : SpotifyCard;
            target.ClientId = id;
            target.ClientSecret = secret;
            target.HideResult();
            CommitSettingsEdit(edited);
        }
    }

    private void OnPreferredChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSettings || PreferredBox.SelectedItem is not string service) return;
        var edited = _settingsShown.Clone();
        edited.PreferredService = service;
        CommitSettingsEdit(edited);
    }

    private void OnCountryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSettings || CountryBox.SelectedItem is not CountryOption country) return;
        var edited = _settingsShown.Clone();
        edited.CountryCode = country.Code;
        CommitSettingsEdit(edited);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSettings || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        _ui.Theme = theme;
        _ui.Save();
        ApplyTheme();
    }

    /// <summary>Text recognition: what Windows has installed, which one reads photos, what can be added.</summary>
    private void FillOcrSettings()
    {
        bool wasFilling = _fillingSettings;
        _fillingSettings = true;
        try
        {
            var installed = OcrService.InstalledTags;
            var choices = new List<OcrLanguageOption> { new("auto", "Automatic (Spanish when installed)") };
            choices.AddRange(installed.Select(t => new OcrLanguageOption(t, OcrLanguages.NameOf(t))));
            OcrLanguageBox.ItemsSource = choices;
            OcrLanguageBox.SelectedItem = choices.FirstOrDefault(c => c.Tag.Equals(_ui.OcrLanguage, StringComparison.OrdinalIgnoreCase))
                                          ?? choices[0];

            var pending = OcrService.PendingRestartTags;
            var names = installed.Select(OcrLanguages.NameOf)
                .Concat(pending.Select(t => $"{OcrLanguages.NameOf(t)} (after restarting Queueify)")).ToList();
            OcrInstalledList.Text = names.Count == 0
                ? "None. Photos can't be read until a language is installed."
                : string.Join(", ", names);

            // The button and progress follow the install actually running, wherever it was started.
            var installable = OcrLanguages.Installable(installed, pending);
            bool installing = OcrService.InstallingTag != null;
            OcrInstallBox.ItemsSource = installable;
            OcrInstallBox.SelectedIndex = installable.Count > 0 ? 0 : -1;
            OcrInstallBox.IsEnabled = OcrInstallButton.IsEnabled = installable.Count > 0 && !installing;
            OcrInstallProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
            OcrInstallBox.PlaceholderText = installable.Count > 0 ? "" : "All offered languages are installed";
        }
        finally
        {
            _fillingSettings = wasFilling;
        }
    }

    private void OnOcrLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSettings || OcrLanguageBox.SelectedItem is not OcrLanguageOption choice) return;
        _ui.OcrLanguage = choice.Tag;
        _ui.Save();
    }

    private async void OnInstallOcrLanguage(object sender, RoutedEventArgs e)
    {
        if (OcrInstallBox.SelectedItem is not OcrLanguageOption choice) return;
        OcrInstallInfo.IsOpen = false;
        try
        {
            // Retry and Windows settings are already on this page; only a restart needs a button here.
            await InstallOcrLanguageAsync(choice.Tag, (severity, message, offer) =>
            {
                OcrInstallInfo.Severity = severity;
                OcrInstallInfo.Message = message;
                OcrInstallInfo.ActionButton = offer.HasFlag(OcrOffer.Restart) ? RestartButton() : null;
                OcrInstallInfo.IsOpen = true;
            });
        }
        finally
        {
            FillOcrSettings();
        }
    }

    private async void OnOpenWindowsLanguages(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(new Uri("ms-settings:regionlanguage"));

    private void ApplyTheme()
    {
        Root.RequestedTheme = _ui.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        UpdateCaptionButtons();
    }

    /// <summary>The window's own minimize/maximize/close buttons don't follow the app theme by themselves.</summary>
    private void UpdateCaptionButtons()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        var text = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Color.FromArgb(0, 0, 0, 0);
        bar.ButtonForegroundColor = bar.ButtonHoverForegroundColor = bar.ButtonPressedForegroundColor = text;
        bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 128, 128, 128);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0);
    }

    private void OnResetLayout(object sender, RoutedEventArgs e)
    {
        _ui.ResetLayout();
        _ui.Save();
        CoverViewToggle.IsChecked = _ui.CoverView;
        CheckSortItem();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } presenter) presenter.Restore();
        SizeToScreen(1440, 920);
        GoTo(Page.Queue);
        ShowToast("Layout reset: the panel, the list view, the order and the window are back as they started.",
            severity: InfoBarSeverity.Informational);
    }

    private static TextBlock Paragraph(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
}
