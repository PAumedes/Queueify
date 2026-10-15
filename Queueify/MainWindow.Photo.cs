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
            ShowOcrInfo(InfoBarSeverity.Error, Loc.F("photo.open_failed", ex.Message));
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
            ShowOcrInfo(InfoBarSeverity.Informational, Loc.T("photo.clipboard_empty"));
        }
    }

    private void OnPhotoDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Bitmap))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.T("photo.drop_caption");
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
        ShowOcrInfo(InfoBarSeverity.Informational, Loc.T("photo.reading"));

        OcrPage page;
        try
        {
            page = await OcrService.RecognizeAsync(stream, _ui.OcrLanguage);
        }
        catch (Exception ex)
        {
            ShowOcrInfo(InfoBarSeverity.Error,
                Loc.F("photo.read_failed", ex.Message));
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
        var stroke = new SolidColorBrush(accent);
        var style = (Style)Root.Resources["PhotoLineButton"];
        foreach (var line in page.Lines)
        {
            var box = new Button
            {
                Style = style,
                Width = line.Bounds.Width + 8,
                Height = line.Bounds.Height + 6,
                Background = idle,
                BorderBrush = stroke,
                Tag = line.Text,
            };
            AutomationProperties.SetName(box, line.Text);
            ToolTipService.SetToolTip(box, line.Text);
            box.Click += OnLineClicked;
            Canvas.SetLeft(box, line.Bounds.X - 4);
            Canvas.SetTop(box, line.Bounds.Y - 3);
            LineCanvas.Children.Add(box);
        }

        _startFreshFromPhoto = true;
        if (page.Lines.Count == 0)
            ShowOcrInfo(InfoBarSeverity.Warning, Loc.T("photo.no_text"));
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
    private void OnLineClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string raw } box) return;
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
        // Installed earlier in this run: it only needs Queueify to restart, not another install.
        if (OcrService.PendingRestartTags.FirstOrDefault(t => t.StartsWith("es", StringComparison.OrdinalIgnoreCase)) is string pending)
        {
            ShowPhotoOcrOffer(InfoBarSeverity.Informational,
                Loc.F("ocr.installed_restart_app", OcrLanguages.NameInSentence(pending)), OcrOffer.Restart | OcrOffer.Dismiss);
            return;
        }
        ShowPhotoOcrOffer(InfoBarSeverity.Warning,
            usedLanguage != null ? Loc.F("ocr.no_spanish_as", usedLanguage) : Loc.T("ocr.no_spanish"),
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
            var install = new Button { Content = Loc.T("ocr.install_spanish") };
            install.Click += async (_, _) =>
            {
                install.IsEnabled = false;
                install.Content = Loc.T("ocr.installing");
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
            var windows = new HyperlinkButton { Content = Loc.T("settings.ocr.windows") };
            windows.Click += OnOpenWindowsLanguages;
            links.Children.Add(windows);
        }
        if (offer.HasFlag(OcrOffer.Dismiss) && !_ui.OcrHintDismissed)
        {
            var dismiss = new HyperlinkButton { Content = Loc.T("ocr.dismiss") };
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
        var name = OcrLanguages.NameInSentence(tag);
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
                report(InfoBarSeverity.Success, Loc.F("ocr.installed", name), offer);
                break;
            case OcrInstallResult.RestartApp:
                report(InfoBarSeverity.Success, Loc.F("ocr.installed_restart_app", name), offer);
                break;
            case OcrInstallResult.RestartWindows:
                report(InfoBarSeverity.Warning, Loc.F("ocr.installed_restart_windows", name), offer);
                break;
            case OcrInstallResult.Cancelled:
                report(InfoBarSeverity.Informational,
                    Loc.T("ocr.cancelled"), offer);
                break;
            case OcrInstallResult.Interrupted:
                report(InfoBarSeverity.Warning,
                    Loc.T("ocr.interrupted"), offer);
                break;
            case OcrInstallResult.Busy:
                report(InfoBarSeverity.Informational, Loc.T("ocr.busy"), offer);
                break;
            case OcrInstallResult.Blocked:
                report(InfoBarSeverity.Warning,
                    Loc.F("ocr.blocked", outcome.Error), offer);
                break;
            default:
                var detail = outcome.Error != null ? Loc.F("ocr.windows_said", outcome.Error) : "";
                report(InfoBarSeverity.Error, Loc.F("ocr.failed", name, detail), offer);
                break;
        }
        if (_page == Page.Settings) FillOcrSettings();
    }

    private Button RestartButton()
    {
        var restart = new Button { Content = Loc.T("app.restart") };
        restart.Click += (_, _) => _ = RestartAppAsync();
        return restart;
    }

    private enum Restart { Closed, NotStarted, StarterFailed }

    /// <summary>
    /// Starts a fresh copy and closes this one (which saves the window and layout as usual). Through the
    /// download's starter, this window stays until the starter has unpacked the new copy and its window is
    /// open, so there's never a moment without Queueify on screen, nor two of them.
    /// </summary>
    private async Task<Restart> RestartAppAsync()
    {
        // A second click while the new copy starts would start another one.
        if (_restarting) return Restart.Closed;
        _restarting = true;
        // Saved first, so the new copy opens where this one was.
        SaveWindow();
        System.Diagnostics.Process? started;
        var running = QueueifyProcesses();
        try
        {
            // Through the download's starter when there is one, so the new copy knows it too.
            started = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppData.StarterExe ?? Environment.ProcessPath!) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowToast(Loc.F("app.restart_failed", ex.Message), severity: InfoBarSeverity.Warning);
            _restarting = false;
            return Restart.NotStarted;
        }
        using (started)
        {
            if (started != null && AppData.StarterExe != null)
            {
                // However long the first unpack takes, or the starter's message waits to be read: until
                // it's done, nothing else is running.
                await started.WaitForExitAsync();
                if (started.ExitCode != 0)
                {
                    // The starter showed its own message about what went wrong.
                    _restarting = false;
                    return Restart.StarterFailed;
                }
                // The new copy runs; its window takes a moment more. A minute at most, then this one goes anyway.
                for (int i = 0; i < 300 && !NewQueueifyWindow(running); i++)
                    await Task.Delay(200);
            }
        }
        Close();
        return Restart.Closed;
    }
    private bool _restarting;
}
