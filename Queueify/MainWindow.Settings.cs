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
            CountryBox.ItemsSource = Items(countries);
            CountryBox.SelectedItem = countries.FirstOrDefault(c => c.Code == shown.CountryCode);
            ThemeBox.SelectedItem = ThemeBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _ui.Theme)
                                    ?? ThemeBox.Items[0];
            LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _ui.Language)
                                       ?? LanguageBox.Items[0];
            UpdateLanguageRestart();
            UpdateCheckToggle.IsOn = _ui.CheckForUpdates;
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
                card.ShowState(false, false, Loc.T("service.not_connected.pill"), Loc.F("service.not_connected", service));
            else if (_testing.Contains(service))
                card.ShowState(false, true, Loc.T("service.testing.pill"), Loc.T("service.testing"));
            else if (_serviceProblem.TryGetValue(service, out var problem))
                card.ShowState(false, true, Loc.T(problem.KeysRejected ? "service.check_keys.pill" : "service.unreachable.pill"), problem.Message);
            else if (_verified.Contains(service))
                card.ShowState(true, true, Loc.T("service.connected.pill"), Loc.F("service.connected", service));
            else
                card.ShowState(true, true, Loc.T("service.saved.pill"), Loc.F("service.saved", service));
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
            card.ShowResult(InfoBarSeverity.Warning, Loc.T("service.need_both"));
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
        catch (OperationCanceledException) { problem = (Loc.F("service.timeout", service), false); }
        catch (HttpRequestException ex) { problem = (Loc.F("service.unreachable", service, ex.Message), false); }
        catch (Exception ex) { problem = (Loc.F("service.unexpected", service, ex.Message), false); }
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
            card.ShowResult(InfoBarSeverity.Success, Loc.F("service.test_ok", QueueText.Albums(found)));
    }

    private void OnRemoveServiceKeys(object? sender, EventArgs e)
    {
        if (sender is not ServiceCard card) return;
        var service = card == TidalCard ? MusicService.Tidal : MusicService.Spotify;
        var (oldId, oldSecret) = service == MusicService.Tidal
            ? (_settings.TidalClientId, _settings.TidalClientSecret)
            : (_settings.SpotifyClientId, _settings.SpotifyClientSecret);

        SetKeys(service, "", "");
        ShowToast(Loc.F("service.removed", service), undo: () =>
        {
            // Keys saved here or synced from another PC since then are newer: undo must not replace them.
            if (KeysOf(service) != ":")
            {
                ShowToast(Loc.F("service.undo_refused", service), severity: InfoBarSeverity.Warning);
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

    private void OnUpdateCheckToggled(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings) return;
        _ui.CheckForUpdates = UpdateCheckToggle.IsOn;
        _ui.Save();
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
            var choices = new List<OcrLanguageOption> { new("auto", Loc.T("ocr.auto")) };
            choices.AddRange(installed.Select(t => new OcrLanguageOption(t, OcrLanguages.NameOf(t))));
            OcrLanguageBox.ItemsSource = Items(choices);
            OcrLanguageBox.SelectedItem = choices.FirstOrDefault(c => c.Tag.Equals(_ui.OcrLanguage, StringComparison.OrdinalIgnoreCase))
                                          ?? choices[0];

            var pending = OcrService.PendingRestartTags;
            var names = installed.Select(OcrLanguages.NameOf)
                .Concat(pending.Select(t => Loc.F("ocr.after_restart", OcrLanguages.NameOf(t)))).ToList();
            OcrInstalledList.Text = names.Count == 0
                ? Loc.T("ocr.none_installed")
                : string.Join(", ", names);

            // The button and progress follow the install actually running, wherever it was started.
            var installable = OcrLanguages.Installable(installed, pending);
            bool installing = OcrService.InstallingTag != null;
            OcrInstallBox.ItemsSource = Items(installable);
            OcrInstallBox.SelectedIndex = installable.Count > 0 ? 0 : -1;
            OcrInstallBox.IsEnabled = OcrInstallButton.IsEnabled = installable.Count > 0 && !installing;
            OcrInstallProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
            OcrInstallBox.PlaceholderText = installable.Count > 0 ? "" : Loc.T("ocr.all_installed");
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

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingSettings || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        _ui.Language = language;
        _ui.Save();
        UpdateLanguageRestart();
    }

    /// <summary>The window's texts are set when it loads, so a new language shows after a restart.</summary>
    private void UpdateLanguageRestart()
    {
        bool pending = Loc.Resolve(_ui.Language, Loc.WindowsLanguage) != Loc.Language;
        LanguageRestartInfo.ActionButton = pending ? RestartButton() : null;
        LanguageRestartInfo.IsOpen = pending;
        LanguageRow.RowSpacing = pending ? 12 : 0;
    }

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
        ShowToast(Loc.T("settings.layout.done"),
            severity: InfoBarSeverity.Informational);
    }

    private static TextBlock Paragraph(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
}
