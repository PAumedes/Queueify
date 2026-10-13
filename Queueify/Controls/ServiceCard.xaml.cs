using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Queueify.Controls;

/// <summary>
/// Settings card for one music service. It only shows and collects values; MainWindow saves the
/// keys and tests them when <see cref="SaveRequested"/> or <see cref="RemoveRequested"/> fires.
/// </summary>
public sealed partial class ServiceCard : UserControl
{
    private string _service = "";

    public ServiceCard()
    {
        InitializeComponent();
    }

    public event EventHandler? SaveRequested;
    public event EventHandler? RemoveRequested;

    /// <summary>"Tidal" or "Spotify"; also names the card's parts for UI Automation (TidalCard, TidalSave, …).</summary>
    public string Service
    {
        get => _service;
        set
        {
            _service = value;
            Logo.Service = value;
            NameText.Text = value;
            AutomationProperties.SetName(Card, value);
            AutomationProperties.SetAutomationId(Card, $"{value}Card");
            AutomationProperties.SetAutomationId(StatusText, $"{value}Status");
            AutomationProperties.SetAutomationId(ClientIdBox, $"{value}ClientId");
            AutomationProperties.SetAutomationId(ClientSecretBox, $"{value}ClientSecret");
            AutomationProperties.SetAutomationId(SaveButton, $"{value}Save");
        }
    }

    public string Steps
    {
        get => StepsText.Text;
        set => StepsText.Text = value;
    }

    public string DashboardUrl
    {
        get => DashboardLink.NavigateUri?.ToString() ?? "";
        set
        {
            DashboardLink.NavigateUri = new Uri(value);
            DashboardLink.Content = value.Replace("https://", "");
        }
    }

    public string ClientId
    {
        get => ClientIdBox.Text.Trim();
        set => ClientIdBox.Text = value;
    }

    public string ClientSecret
    {
        get => ClientSecretBox.Password.Trim();
        set => ClientSecretBox.Password = value;
    }

    /// <summary>Header state: connected (accent pill) or not, with a one-line explanation.</summary>
    public void ShowState(bool connected, string pill, string status)
    {
        OnPill.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        OffPill.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        OnPillText.Text = OffPillText.Text = pill;
        StatusText.Text = status;
        AutomationProperties.SetName(Card, $"{Service}: {status}");
        RemoveButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowResult(InfoBarSeverity severity, string message)
    {
        Result.Severity = severity;
        Result.Message = message;
        Result.IsOpen = true;
    }

    public void HideResult() => Result.IsOpen = false;

    public bool IsBusy
    {
        get => Testing.IsActive;
        set
        {
            Testing.IsActive = value;
            SaveButton.IsEnabled = RemoveButton.IsEnabled = !value;
        }
    }

    public bool IsExpanded
    {
        get => Card.IsExpanded;
        set => Card.IsExpanded = value;
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this, EventArgs.Empty);
}
