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

public sealed partial class MainWindow
{
    // ───────────────────────── Sync between PCs ─────────────────────────

    private void ScheduleSync()
    {
        if (!_settings.SyncConfigured) return;
        _syncDebounce.Stop();
        _syncDebounce.Start();
    }

    private Task<string?>? _syncTask;
    private Task<string?>? _followUpSync;
    private bool _syncSetupRunning;   // the setup dialog is trying a connection: no other sync may run

    /// <summary>
    /// Downloads other PCs' changes and uploads this PC's. Returns an error message, or null when it
    /// worked (or sync isn't set up). If a sync is already running, one more runs right after it and
    /// the caller gets that result, so "Sync now" never reports success for a sync that didn't happen.
    /// Failures are harmless: pending changes stay and go up next time.
    /// </summary>
    private Task<string?> SyncNowAsync()
    {
        if (!_settings.SyncConfigured || _syncSetupRunning) return Task.FromResult<string?>(null);
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
        SyncNav.Content = Loc.T("sync.syncing");

        // Sync a copy: the UI thread keeps using _settings while the sync runs in the background.
        var work = _settings.Clone();
        var provider = Provider();
        var sharedStampBefore = _settings.SharedSettingsUpdatedAt;
        string? error = null;
        try
        {
            var result = await Task.Run(() => provider.SyncAsync(_store, work, CancellationToken.None));
            if (_settings.SyncIdentity == work.SyncIdentity)   // still connected to the same queue
            {
                _settings.CloudLastServerTime = work.CloudLastServerTime;
                _settings.WebLastServerTime = work.WebLastServerTime;
                _settings.WebSettingsSyncedAt = work.WebSettingsSyncedAt;
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

        _syncError = _settings.SyncConfigured ? error : null;
        UpdateSyncButton();
        return error;
    }

    private static string DescribeSyncError(Exception ex) => ex switch
    {
        CatalogException or SyncException => ex.Message,
        HttpRequestException => Loc.T("sync.err.offline"),
        TaskCanceledException => Loc.T("sync.err.timeout"),
        IOException or UnauthorizedAccessException => Loc.F("sync.err.disk", ex.Message),
        _ => Loc.F("sync.err.other", ex.Message),
    };

    /// <summary>The Sync icon in the navigation bar shows the state; its tooltip says why.</summary>
    private void UpdateSyncButton()
    {
        string label, glyph, tip;
        bool web = _settings.WebConfigured;
        if (!_settings.SyncConfigured)
            (label, glyph, tip) = (Loc.T("nav.sync"), "", Loc.T("sync.tip.off"));
        else if (_syncError != null)
            (label, glyph, tip) = (Loc.T("sync.not_synced"), "", Loc.F("sync.tip.error", _syncError));
        else
            (label, glyph, tip) = (Loc.T("sync.synced"), "",
                _lastSynced is DateTime t ? web ? Loc.F("webSync.tip.at", t) : Loc.F("sync.tip.at", t)
                : web ? Loc.T("webSync.tip.ok") : Loc.T("sync.tip.ok"));

        SyncNav.Content = label;
        SyncIcon.Glyph = SettingsSyncIcon.Glyph = glyph;
        SettingsSyncStatus.Text = tip;
        ToolTipService.SetToolTip(SyncNav, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SyncNav, tip);
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        switch (_settings.ActiveSync)
        {
            case SyncKind.Web: await ShowWebSyncConnectedAsync(); break;
            case SyncKind.Cloud: await ShowSyncConnectedAsync(); break;
            default: await ShowSyncSetupAsync(); break;
        }
    }

    /// <summary>First-time setup: create the online queue (first PC) or join it with a sync code.</summary>
    private async Task ShowSyncSetupAsync()
    {
        var create = new RadioButton { Content = Loc.T("sync.setup.create"), GroupName = "sync", IsChecked = true };
        var join = new RadioButton { Content = Loc.T("sync.setup.join"), GroupName = "sync" };
        var web = new RadioButton { Content = Loc.T("webSync.setup.option"), GroupName = "sync" };

        var projectBox = new TextBox { Header = Loc.T("sync.project"), PlaceholderText = Loc.T("sync.project.placeholder") };
        var copyRules = new Button { Content = Loc.T("sync.copy_rules") };
        copyRules.Click += (_, _) =>
        {
            CopyToClipboard(CloudSync.SecurityRules);
            copyRules.Content = Loc.T("sync.rules_copied");
        };
        var createPanel = new StackPanel { Spacing = 10 };
        createPanel.Children.Add(Paragraph(
            Loc.T("sync.setup.steps")));
        createPanel.Children.Add(new HyperlinkButton { Content = Loc.T("sync.console"), NavigateUri = new Uri("https://console.firebase.google.com/") });
        createPanel.Children.Add(copyRules);
        createPanel.Children.Add(projectBox);

        var codeBox = new TextBox { Header = Loc.T("sync.code"), PlaceholderText = "queueify:…" };
        var joinPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        joinPanel.Children.Add(Paragraph(
            Loc.T("sync.setup.join_text")));
        joinPanel.Children.Add(codeBox);

        var webBox = new TextBox { Header = Loc.T("webSync.code"), PlaceholderText = Loc.T("webSync.code.placeholder") };
        var webPanel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        webPanel.Children.Add(Paragraph(Loc.T("webSync.setup.text")));
        webPanel.Children.Add(webBox);
        webPanel.Children.Add(Paragraph(Loc.T("webSync.keys_notice")));

        void ShowOnly(StackPanel shown)
        {
            foreach (var p in new[] { createPanel, joinPanel, webPanel })
                p.Visibility = p == shown ? Visibility.Visible : Visibility.Collapsed;
        }
        create.Checked += (_, _) => ShowOnly(createPanel);
        join.Checked += (_, _) => ShowOnly(joinPanel);
        web.Checked += (_, _) => ShowOnly(webPanel);

        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };

        var panel = new StackPanel { Spacing = 12, Width = 480 };
        panel.Children.Add(Paragraph(Loc.T("sync.setup.intro")));
        panel.Children.Add(create);
        panel.Children.Add(join);
        panel.Children.Add(web);
        panel.Children.Add(createPanel);
        panel.Children.Add(joinPanel);
        panel.Children.Add(webPanel);
        panel.Children.Add(progress);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = Loc.T("sync.title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 600 },
            PrimaryButtonText = Loc.T("sync.connect"),
            CloseButtonText = Loc.T("dialog.cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };

        bool joined = false, toWeb = false;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                string project = "", key = "", webUrl = "", webToken = "";
                toWeb = web.IsChecked == true;
                joined = toWeb || join.IsChecked == true;
                if (toWeb)
                {
                    if (!SyncConnect.TryParse(webBox.Text, out webUrl, out webToken))
                    {
                        Fail(Loc.T("webSync.bad_code"));
                        return;
                    }
                }
                else if (joined)
                {
                    if (!CloudSync.TryParseSyncCode(codeBox.Text, out project, out key))
                    {
                        Fail(Loc.T("sync.bad_code"));
                        return;
                    }
                }
                else
                {
                    if (CloudSync.NormalizeProjectId(projectBox.Text) is not string id)
                    {
                        Fail(Loc.T("sync.no_project"));
                        return;
                    }
                    project = id;
                    key = CloudSync.NewQueueKey();
                }

                SyncTarget target = toWeb ? SyncTarget.Web(webUrl, webToken)
                    : joined ? SyncTarget.JoinCloud(project, key)
                    : SyncTarget.CreateCloud(project, key);

                progress.Visibility = Visibility.Visible;
                error.IsOpen = false;
                d.IsPrimaryButtonEnabled = false;
                string? problem = null;
                AppSettings? trial = null;
                // A debounced sync of the old queue must not run between "mark everything" and the first sync here.
                _syncDebounce.Stop();
                _syncSetupRunning = true;
                try
                {
                    trial = await SyncSetup.TryConnectAsync(_store, _settings, target, _cloud, _web, _syncTask, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    problem = DescribeSyncError(ex);
                }
                finally
                {
                    _syncSetupRunning = false;
                }
                progress.Visibility = Visibility.Collapsed;
                d.IsPrimaryButtonEnabled = true;

                if (trial == null)
                {
                    Fail(problem!);
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

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || !_settings.SyncConfigured) return;

        if (toWeb)
            ShowToast(Loc.F("webSync.joined", QueueText.Albums(_all.Count)));
        else if (joined)
            ShowToast(Loc.F("sync.joined", QueueText.Albums(_all.Count)));
        else
            await ShowSyncConnectedAsync(justCreated: true);
    }

    /// <summary>Shows the sync code for other PCs, plus "Sync now" and "Disconnect".</summary>
    private async Task ShowSyncConnectedAsync(bool justCreated = false)
    {
        var code = CloudSync.SyncCode(_settings);
        var codeBox = new TextBox { Header = Loc.T("sync.code"), Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var copy = new Button { Content = Loc.T("sync.copy_code") };
        copy.Click += (_, _) =>
        {
            CopyToClipboard(code);
            copy.Content = Loc.T("sync.copied");
        };

        var status = _syncError ?? (_lastSynced is DateTime t ? Loc.F("sync.last", t) : Loc.T("sync.ok"));
        var panel = new StackPanel { Spacing = 12, Width = 480 };
        panel.Children.Add(Paragraph(justCreated
            ? Loc.T("sync.done")
            : Loc.F("sync.this_pc", _settings.CloudProjectId, status)));
        panel.Children.Add(Paragraph(
            Loc.T("sync.add_pc")));
        panel.Children.Add(codeBox);
        panel.Children.Add(copy);
        panel.Children.Add(Paragraph(Loc.T("sync.private")));

        var dialog = new ContentDialog
        {
            Title = Loc.T("sync.title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = Loc.T("sync.now"),
            SecondaryButtonText = Loc.T("sync.disconnect"),
            CloseButtonText = Loc.T("dialog.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };

        switch (await ShowDialogAsync(dialog))
        {
            case ContentDialogResult.Primary:
                var problem = await SyncNowAsync();
                ShowToast(problem ?? Loc.T("sync.ok"), severity: problem == null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                break;
            case ContentDialogResult.Secondary:
                _settings.DisconnectCloud();
                _syncError = null;
                AppData.SaveSettings(_settings);
                UpdateSyncButton();
                ShowToast(Loc.T("sync.disconnected"),
                    severity: InfoBarSeverity.Informational);
                break;
        }
    }

    /// <summary>Shows which web server this PC syncs with, plus "Sync now" and "Disconnect".</summary>
    private async Task ShowWebSyncConnectedAsync()
    {
        // Only the host is shown; the token stays out of the window.
        var host = Uri.TryCreate(_settings.WebServerUrl, UriKind.Absolute, out var address) ? address.Authority : "?";
        var status = _syncError ?? (_lastSynced is DateTime t ? Loc.F("sync.last", t) : Loc.T("sync.ok"));
        var panel = new StackPanel { Spacing = 12, Width = 480 };
        panel.Children.Add(Paragraph(Loc.F("webSync.this_pc", host, status)));
        panel.Children.Add(Paragraph(Loc.T("webSync.private")));

        var dialog = new ContentDialog
        {
            Title = Loc.T("sync.title"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = Loc.T("sync.now"),
            SecondaryButtonText = Loc.T("sync.disconnect"),
            CloseButtonText = Loc.T("dialog.close"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };

        switch (await ShowDialogAsync(dialog))
        {
            case ContentDialogResult.Primary:
                var problem = await SyncNowAsync();
                ShowToast(problem ?? Loc.T("sync.ok"), severity: problem == null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                break;
            case ContentDialogResult.Secondary:
                _settings.DisconnectWeb();
                _syncError = null;
                AppData.SaveSettings(_settings);
                UpdateSyncButton();
                ShowToast(Loc.T("webSync.disconnected"), severity: InfoBarSeverity.Informational);
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
}
