using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
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
    private readonly ServerSync _web = new(ServerSync.CreateClient());
    private readonly Updater _updater = new(Http, UpdateSource.For(Environment.GetEnvironmentVariable("QUEUEIFY_UPDATE_SERVER")));
    private bool _updating;

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
        if (AppData.StarterExe is { } starter)
        {
            TaskbarIdentity.PinTo(WinRT.Interop.WindowNative.GetWindowHandle(this), starter);
            Updater.RemoveLeftovers(starter);
        }

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
        // The line under the search box shows only while it has something to say.
        SearchStatus.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateSearchStatusPanel());
        SearchProgress.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateSearchStatusPanel());
        AddWithoutLink.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateSearchStatusPanel());
        // The bar keeps its height from state to state while open; a new window size starts it over.
        UpdateBar.Closed += (_, _) => UpdateBar.MinHeight = 0;
        SizeChanged += (_, _) => UpdateBar.MinHeight = 0;

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
            if (_ui.CheckForUpdates) _ = CheckForUpdatesAsync(quiet: true);
            if (!_ui.TourSeen) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => ShowTourStep(0));
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Queueify/1.0");
        return http;
    }

    private ISyncProvider Provider() => SyncProviders.For(_settings.ActiveSync, _cloud, _web);

    // ───────────────────────── Window & layout ─────────────────────────

    private void UpdateSearchStatusPanel() =>
        SearchStatusPanel.Visibility = SearchStatus.Text.Length > 0 || SearchProgress.Visibility == Visibility.Visible
            || AddWithoutLink.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

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
            SearchStatus.Text = Loc.T("search.no_service");
            ShowAddWithoutLink(raw);
            return;
        }

        var text = Matcher.SearchText(raw);
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();

        // While searching, the old results can't be added with Enter (they belong to another query).
        _resultsQuery = null;
        SearchProgress.Visibility = Visibility.Visible;
        SearchStatus.Text = Loc.T("search.searching");
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
            results.Count == 0 ? Loc.T("search.none")
                : confident ? (results[0].CanAdd ? Loc.T("search.press_enter") : Loc.T("search.already"))
                : Loc.T("search.no_close"),
        }.Concat(errors));

        if (!confident) ShowAddWithoutLink(raw);
    }

    private static async Task<(List<AlbumMatch>? Matches, string? Error)> Safe(Task<List<AlbumMatch>> search, CancellationToken ct)
    {
        try { return (await search, null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (null, null); } // replaced by a newer search
        catch (OperationCanceledException) { return (null, Loc.T("search.timeout")); }
        catch (CatalogException ex) { return (null, ex.Message); }
        catch (HttpRequestException ex) { return (null, Loc.F("search.network", ex.Message)); }
        catch (Exception ex) { return (null, Loc.F("search.unexpected", ex.Message)); }
    }

    private void ShowResults(List<AlbumResult> results)
    {
        ResultsList.ItemsSource = Items(results);
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
        AddWithoutLink.Content = Loc.F("search.add_without_link", raw);
        AddWithoutLink.Visibility = Visibility.Visible;
    }

    private string? QueueStateOf(AlbumResult r)
    {
        var saved = QueueView.FindSaved(r, _all);
        return saved == null ? null : saved.IsListened ? Loc.T("search.state.listened") : Loc.T("search.state.queued");
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
            SearchStatus.Text = Loc.F("search.already_saved", r.Title, r.QueueState);
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
        if (QueueView.ParseTyped(SearchBox.Text) is not { } typed) return;
        AddItem(new BacklogItem { Artist = typed.Artist, Album = typed.Album, Year = typed.Year });
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
        ShowToast(Loc.F("toast.added", item.Album), undo: () =>
        {
            if (_store.FindByUid(item.Uid) is { Deleted: false } current) _store.Delete(current);
            ReloadQueue();
        });
        SearchBox.Focus(FocusState.Programmatic);
    }

    // ───────────────────────── Queue ─────────────────────────

    /// <summary>
    /// Lists for ItemsSource as List&lt;object&gt; built here: compiled ahead of time, WinUI can only read a list
    /// whose WinRT interop code was generated at build time, and that happens for lists made in this project,
    /// not for those Queueify.Core returns. The {x:Bind} templates cast each item back.
    /// </summary>
    private static List<object> Items<T>(IEnumerable<T> items) where T : notnull => [.. items.Cast<object>()];

    private void RefreshQueue()
    {
        // Selection/text events can fire while InitializeComponent is still building the UI.
        if (_all is null) return;

        bool queuePage = _page is Page.Queue or Page.Listened;
        bool listened = _page == Page.Listened;
        bool filtering = Matcher.Normalize(FilterBox.Text).Length > 0;
        var list = QueueView.Apply(_all, listened, FilterBox.Text, _ui.Sort);

        QueueList.ItemsSource = _ui.CoverView ? null : Items(list);
        QueueGrid.ItemsSource = _ui.CoverView ? Items(list) : null;
        QueueList.Visibility = _ui.CoverView ? Visibility.Collapsed : Visibility.Visible;
        QueueGrid.Visibility = _ui.CoverView ? Visibility.Visible : Visibility.Collapsed;

        // Round count badges in the navigation bar.
        var pending = _all.Where(i => i.IsPending).ToList();
        var heard = _all.Where(i => i.IsListened).ToList();
        SetBadge(QueueNav, QueueBadge, Loc.T("nav.queue"), pending.Count, "badge.waiting");
        SetBadge(ListenedNav, ListenedBadge, Loc.T("nav.listened"), heard.Count, "badge.listened");

        (PageIcon.Glyph, PageTitle.Text, PageSummary.Text) = _page switch
        {
            Page.Listened => ("", Loc.T("nav.listened"), QueueText.ListenedSummary(heard)),
            Page.Photo => ("", Loc.T("nav.photo"), Loc.T("page.photo.summary")),
            Page.Help => ("", Loc.T("nav.help"), Loc.F("page.help.summary", AppInfo.Name, AppInfo.Version)),
            Page.Settings => ("\uE713", Loc.T("nav.settings"), Loc.T("page.settings.summary")),
            _ => ("", Loc.T("nav.queue"), QueueText.PendingSummary(pending, DateTime.Now)),
        };

        QueuePanel.Visibility = QueueCommands.Visibility = queuePage ? Visibility.Visible : Visibility.Collapsed;
        PhotoPanel.Visibility = PhotoCommands.Visibility = _page == Page.Photo ? Visibility.Visible : Visibility.Collapsed;
        HelpPanel.Visibility = _page == Page.Help ? Visibility.Visible : Visibility.Collapsed;
        // Opening Help clears the dot and goes straight to the update.
        if (_page == Page.Help && UpdateBadge.Visibility == Visibility.Visible)
        {
            SetUpdateBadge(false);
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateBar.StartBringIntoView());
        }
        SettingsPanel.Visibility = _page == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        ApplyPaneLayout();

        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        (EmptyIcon.Glyph, EmptyTitle.Text, EmptyText.Text) =
            filtering ? ("", Loc.T("empty.filter.title"), Loc.T("empty.filter.text"))
            : listened ? ("", Loc.T("empty.listened.title"), Loc.T("empty.listened.text"))
            : ("", Loc.T("empty.queue.title"), Loc.T("empty.queue.text"));
    }

    private static void SetBadge(NavigationViewItem item, InfoBadge badge, string label, int count, string nameKey)
    {
        badge.Value = count;
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, count > 0 ? Loc.F(count == 1 ? nameKey + ".one" : nameKey, label, count) : label);
    }

    /// <summary>
    /// The dot on Help while something about an update waits there: a new version, or a problem with one
    /// (then in the caution colour). Named for screen readers, and in the tooltip.
    /// </summary>
    private void SetUpdateBadge(bool shown, bool problem = false)
    {
        UpdateBadge.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge.Style = problem ? (Style)Application.Current.Resources["CautionDotInfoBadgeStyle"] : null;
        var key = problem ? "nav.help.update_problem" : "nav.help.update";
        var name = shown ? Loc.F(key, Loc.T("nav.help")) : Loc.T("nav.help");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HelpNav, name);
        ToolTipService.SetToolTip(HelpNav, shown ? Loc.F(key, Loc.T("nav.help")) + " (F1)" : Loc.T("nav.help.tip"));
        if (shown) Announce(HelpNav, name);
    }

    /// <summary>A short note at the bottom with a way to Help → About, for update news found on another page.</summary>
    private void ShowUpdateToast(string message, InfoBarSeverity severity) =>
        ShowToast(message, undo: () => GoTo(Page.Help), actionText: Loc.T("update.show"), severity: severity);

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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SortButton, Loc.F("sort.name", SortButton.Label));
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
        (service ?? item.PlayService) is { } which && PlayLinks.Only(item, which) is { } link
            ? ExternalLinks.OpenAsync(link)
            : Task.CompletedTask;

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
        ShowToast(Loc.F(listened ? "toast.listened" : "toast.moved_back", item.Album), undo: () =>
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
        ShowToast(Loc.F("toast.deleted", item.Album), undo: () =>
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

        menu.Items.Add(Entry(Loc.T("menu.open_tidal"), "", () => _ = PlayAsync(item, MusicService.Tidal), item.HasTidal));
        menu.Items.Add(Entry(Loc.T("menu.open_spotify"), "", () => _ = PlayAsync(item, MusicService.Spotify), item.HasSpotify));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(item.IsListened
            ? Entry(Loc.T("menu.move_back"), "", () => SetListened(item, false))
            : Entry(Loc.T("item.mark_listened"), "", () => SetListened(item, true)));
        menu.Items.Add(Entry(Loc.T("menu.delete"), "", () => Delete(item)));
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
            ShowToast(Loc.T("toast.surprise_empty"), severity: InfoBarSeverity.Informational);
            return;
        }
        var pick = pending[Random.Shared.Next(pending.Count)];

        GoTo(Page.Queue);
        FilterBox.Text = "";
        RefreshQueue();
        ListViewBase view = _ui.CoverView ? QueueGrid : QueueList;
        view.SelectedItem = pick;
        view.ScrollIntoView(pick, ScrollIntoViewAlignment.Leading);

        var question = pick.Artist.Length > 0 ? Loc.F("toast.surprise_by", pick.Album, pick.Artist) : Loc.F("toast.surprise", pick.Album);
        ShowToast(question, undo: () => _ = PlayAsync(pick),
            actionText: pick.CanPlay ? Loc.F("toast.play_on", pick.PlayLabel) : null, severity: InfoBarSeverity.Informational);
    }

    private static BacklogItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as BacklogItem;

    /// <summary>Bottom notification with an optional action (usually Undo) that disappears by itself.</summary>
    private void ShowToast(string message, Action? undo = null, string? actionText = "",
        InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        Toast.IsOpen = false;
        Toast.Severity = severity;
        Toast.Message = message;
        Toast.ActionButton = null;
        if (undo != null && actionText != null)
        {
            var button = new Button { Content = actionText.Length > 0 ? actionText : Loc.T("toast.undo") };
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

    // ───────────────────────── Help & quick tour ─────────────────────────

    private void FillHelp()
    {
        ShortcutList.ItemsSource = Items(Shortcuts.All);
        AboutVersion.Text = Loc.F("about.version", AppInfo.Version);
        AboutTagline.Text = AppInfo.Tagline;
        AboutCredits.ItemsSource = Items(AppInfo.Credits);
        AboutDataPath.Text = AppData.Folder;
    }

}
