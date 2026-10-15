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
    // ───────────────────────── Updates ─────────────────────────

    private bool _checkingUpdates;
    /// <summary>Asked from About: the running check shows its result there.</summary>
    private bool _reportCheck;
    private CancellationTokenSource? _download;

    private async void OnCheckForUpdates(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(quiet: false);

    /// <summary>
    /// Asks GitHub for a newer version and offers it under the page title. Quiet (at start) shows nothing
    /// unless there is one; from About, the result shows under the button.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool quiet)
    {
        // One check at a time. Asked from About while the quiet one at start runs, that one reports there
        // when it's done. The button stays enabled, so keyboard focus stays on it.
        if (!quiet)
        {
            _reportCheck = true;
            ShowUpdateStatus(Loc.T("update.checking"));
        }
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        try
        {
            var release = await _updater.CheckAsync(AppInfo.Version, CancellationToken.None);
            if (release != null && !_updating && Updater.IsWanted(release, _ui.SkippedVersion, asked: _reportCheck))
            {
                OfferUpdate(release);
                // Found while on another page: a dot on Help and a short note say so; the offer itself waits
                // in About.
                if (_page != Page.Help)
                {
                    SetUpdateBadge(true);
                    ShowUpdateToast(Loc.F("update.available", release.Version), InfoBarSeverity.Informational);
                }
            }
            // The offer, right above, already says what's new; the line under the button says the rest.
            if (_reportCheck) ShowUpdateStatus(release != null ? "" : Loc.T("update.latest"));
        }
        catch (Exception ex)
        {
            // UpdateException carries its own message; anything else is a bug, still not worth a crash.
            if (_reportCheck) ShowUpdateStatus(ex is UpdateException ? ex.Message : Loc.F("update.failed", ex.Message));
        }
        finally
        {
            _checkingUpdates = false;
            _reportCheck = false;
        }
    }

    private void ShowUpdateStatus(string text)
    {
        UpdateStatus.Text = text;
        if (text.Length == 0) return;
        // At the bottom of Help, maybe below the window's edge; and said aloud for screen readers.
        UpdateStatus.StartBringIntoView();
        Announce(UpdateStatus, text);
    }

    private void OfferUpdate(AppRelease release)
    {
        var whatsNew = new HyperlinkButton { Content = Loc.T("update.whats_new"), Padding = new Thickness(0, 2, 0, 2) };
        whatsNew.Click += (_, _) => OpenInBrowser(release.Page);
        // Not now, and not at the next starts either; Check for updates in About still finds it.
        var skip = new HyperlinkButton { Content = Loc.T("update.skip"), Padding = new Thickness(0, 2, 0, 2) };
        AutomationProperties.SetAutomationId(skip, "SkipUpdateButton");
        skip.Click += (_, _) =>
        {
            _ui.SkippedVersion = release.Version;
            _ui.Save();
            UpdateBar.IsOpen = false;
            SetUpdateBadge(false);
        };
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(0, 0, 0, 8), Children = { whatsNew, skip } };
        ShowUpdateBar(InfoBarSeverity.Informational, Loc.F("update.available", release.Version), InstallButton(release, "update.install"), links, closable: true);
    }

    private Button InstallButton(AppRelease release, string text)
    {
        var install = new Button { Content = Loc.T(text), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(install, "InstallUpdateButton");
        install.Click += (_, _) => _ = InstallUpdateAsync(release);
        return install;
    }

    /// <summary>Downloads the new exe, checks it, puts it in place of this one and restarts through it.</summary>
    private async Task InstallUpdateAsync(AppRelease release)
    {
        if (_updating) return;
        if (AppData.StarterExe is not { } exe)
        {
            // Running from a build folder, not a download: nothing to replace.
            ShowUpdateProblem(Loc.T("update.manual"), release, retry: false);
            return;
        }
        _updating = true;
        var message = Loc.F("update.downloading", release.Version);
        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(bar, message);
        // No overall time limit: a slow download that keeps coming is fine, a stalled one gives up, and
        // Cancel stops either.
        var cancel = new Button { Content = Loc.T("update.cancel") };
        cancel.Click += (_, _) => _download?.Cancel();
        ShowUpdateBar(InfoBarSeverity.Informational, message, cancel, bar, closable: false);
        _download = new CancellationTokenSource();
        try
        {
            var downloaded = await _updater.DownloadAsync(release, exe, new Progress<double>(v => bar.Value = v), _download.Token);
            Updater.Replace(exe, downloaded);
        }
        catch (OperationCanceledException) when (_download.IsCancellationRequested)
        {
            _updating = false;
            OfferUpdate(release);
            return;
        }
        catch (Exception ex)
        {
            _updating = false;
            ShowUpdateProblem(ex is UpdateException ? ex.Message : Loc.F("update.failed", ex.Message), release, retry: true);
            return;
        }
        finally
        {
            _download.Dispose();
            _download = null;
        }
        // The new version unpacks before its window opens: this one says so and stays until then. The
        // keyboard waits on the progress indicator (named, so it's read out) instead of falling into the page.
        var starting = Loc.F("update.restarting", release.Version);
        var wait = new ProgressBar { IsIndeterminate = true, IsTabStop = true, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(wait, starting);
        ShowUpdateBar(InfoBarSeverity.Informational, starting, null, wait, closable: false);
        switch (await RestartAppAsync())
        {
            case Restart.StarterFailed:
            {
                // The new exe itself didn't start (its starter said why), and it's the user's only Queueify
                // exe now. What helps first is a version that works, maybe an older one if this release is
                // broken: all of them are on the releases page. The same download again only helps when the
                // cause was on this PC (disk space, an antivirus).
                _updating = false;
                var releases = new Button { Content = Loc.T("update.releases"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                releases.Click += (_, _) => OpenInBrowser(UpdateSource.AllReleases);
                var retry = new HyperlinkButton { Content = Loc.T("update.retry"), Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 0, 8) };
                AutomationProperties.SetAutomationId(retry, "InstallUpdateButton");
                retry.Click += (_, _) => _ = InstallUpdateAsync(release);
                // Too important to wait behind a dot: the user mustn't close Queueify without reading it.
                GoTo(Page.Help);
                ShowUpdateBar(InfoBarSeverity.Error, Loc.F("update.start_failed", release.Version), releases, retry, closable: false);
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => UpdateBar.StartBringIntoView());
                break;
            }
            case Restart.NotStarted:
            {
                // The new exe is in place; the next start uses it. _updating stays set, so it isn't
                // downloaded again. OK closes the bar, and gives the keyboard somewhere to be meanwhile.
                var ok = new Button { Content = Loc.T("update.ok") };
                ok.Click += (_, _) => UpdateBar.IsOpen = false;
                ShowUpdateBar(InfoBarSeverity.Warning, Loc.F("update.restart_failed", release.Version), ok, null, closable: true);
                break;
            }
        }
    }

    private static HashSet<int> QueueifyProcesses()
    {
        var ids = new HashSet<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Queueify"))
            using (p) ids.Add(p.Id);
        return ids;
    }

    /// <summary>A Queueify window from a process that wasn't running before (another copy open already doesn't count).</summary>
    private static bool NewQueueifyWindow(HashSet<int> before)
    {
        var found = false;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("Queueify"))
            using (p)
                found |= !before.Contains(p.Id) && p.MainWindowHandle != 0;
        return found;
    }

    private void ShowUpdateProblem(string message, AppRelease release, bool retry)
    {
        var page = new HyperlinkButton { Content = Loc.T("update.page"), Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 0, 8) };
        page.Click += (_, _) => OpenInBrowser(release.Page);
        ShowUpdateBar(InfoBarSeverity.Warning, message, retry ? InstallButton(release, "update.retry") : null, page, closable: true);
    }

    private void ShowUpdateBar(InfoBarSeverity severity, string message, Microsoft.UI.Xaml.Controls.Primitives.ButtonBase? action, UIElement? content, bool closable)
    {
        // Keyboard focus in the bar (on the button just pressed) moves to the new button (Cancel, Try again)
        // instead of falling into the page when that button goes away. Anywhere else, it stays: someone
        // typing in the search box keeps typing there, and a key never presses Try again by surprise.
        var focusInBar = IsInside(FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject, UpdateBar);
        var wasOpen = UpdateBar.IsOpen;
        UpdateBar.Severity = severity;
        UpdateBar.Message = message;
        UpdateBar.ActionButton = action;
        UpdateBar.Content = content;
        UpdateBar.IsClosable = closable;
        UpdateBar.IsOpen = true;
        // A problem while the user is on another page: the dot comes back, and a note says what happened.
        if (_page != Page.Help && severity is InfoBarSeverity.Warning or InfoBarSeverity.Error)
        {
            SetUpdateBadge(true, problem: true);
            ShowUpdateToast(message, severity);
        }
        // Opening, the bar announces itself; later states are said here.
        if (wasOpen) Announce(UpdateBar, message);
        // Only grows while open, so the page below doesn't jump up and down from one state to the next.
        if (wasOpen) UpdateBar.MinHeight = Math.Max(UpdateBar.MinHeight, UpdateBar.ActualHeight);
        if (focusInBar && ((Control?)action ?? content as Control) is { IsTabStop: true } target)
            target.Loaded += (_, _) => target.Focus(FocusState.Keyboard);
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (; element != null; element = VisualTreeHelper.GetParent(element))
            if (element == container) return true;
        return false;
    }

    private static void Announce(UIElement element, string text) =>
        (FrameworkElementAutomationPeer.FromElement(element) ?? FrameworkElementAutomationPeer.CreatePeerForElement(element))
            ?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, text, "QueueifyUpdate");
    private static void OpenInBrowser(Uri page) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppData.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppData.Folder) { UseShellExecute = true });
    }

    private int _tourStep;

    /// <summary>The four stops of the quick tour: what to point at and what to say.</summary>
    private (FrameworkElement Target, string Title, string Text)[] TourSteps =>
    [
        (SearchBox, Loc.T("pane.search"), Loc.T("tour.1")),
        (QueueNav, Loc.T("tour.2.title"), Loc.T("tour.2")),
        (PhotoNav, Loc.T("tour.3.title"), Loc.T("tour.3")),
        (SyncNav, Loc.T("tour.4.title"), Loc.T("tour.4")),
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
        Tour.Title = Loc.F("tour.counter", title, step + 1, steps.Length);
        Tour.Subtitle = text;
        Tour.ActionButtonContent = Loc.T(step == steps.Length - 1 ? "tour.done" : "tour.next");
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
}
