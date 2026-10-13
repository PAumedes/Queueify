using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Queueify.Controls;

/// <summary>
/// Spotify's or Tidal's mark on a rounded dark tile, drawn from vector paths so it stays sharp at
/// any size and scale. Paths: Simple Icons (CC0).
/// </summary>
public sealed partial class ServiceLogo : Grid
{
    private const string SpotifyPath =
        "M12 0C5.4 0 0 5.4 0 12s5.4 12 12 12 12-5.4 12-12S18.66 0 12 0zm5.521 17.34c-.24.359-.66.48-1.021.24-2.82-1.74-6.36-2.101-10.561-1.141-.418.122-.779-.179-.899-.539-.12-.421.18-.78.54-.9 4.56-1.021 8.52-.6 11.64 1.32.42.18.479.659.301 1.02zm1.44-3.3c-.301.42-.841.6-1.262.3-3.239-1.98-8.159-2.58-11.939-1.38-.479.12-1.02-.12-1.14-.6-.12-.48.12-1.021.6-1.141C9.6 9.9 15 10.561 18.72 12.84c.361.181.54.78.241 1.2zm.12-3.36C15.24 8.4 8.82 8.16 5.16 9.301c-.6.179-1.2-.181-1.38-.721-.18-.601.18-1.2.72-1.381 4.26-1.26 11.28-1.02 15.721 1.621.539.3.719 1.02.419 1.56-.299.421-1.02.599-1.559.3z";

    private const string TidalPath =
        "M12.012 3.992L8.008 7.996 4.004 3.992 0 7.996 4.004 12l4.004-4.004L12.012 12l-4.004 4.004 4.004 4.004 4.004-4.004L12.012 12l4.004-4.004-4.004-4.004zM16.042 7.996l3.979-3.979L24 7.996l-3.979 3.979z";

    private string _service = "";

    public ServiceLogo()
    {
        Width = Height = 40;
        CornerRadius = new CornerRadius(8);
        Background = new SolidColorBrush(Color.FromArgb(255, 18, 18, 18));
        Padding = new Thickness(8);
    }

    /// <summary>"Spotify" or "Tidal".</summary>
    public string Service
    {
        get => _service;
        set
        {
            _service = value;
            bool spotify = value == "Spotify";
            Children.Clear();
            Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), spotify ? SpotifyPath : TidalPath),
                Stretch = Stretch.Uniform,
                Fill = new SolidColorBrush(spotify ? Color.FromArgb(255, 30, 215, 96) : Microsoft.UI.Colors.White),
            });
            AutomationProperties.SetName(this, $"{value} logo");
        }
    }

    // A plain panel is invisible to screen readers; the logo is announced as an image.
    protected override AutomationPeer OnCreateAutomationPeer() => new LogoPeer(this);

    private sealed partial class LogoPeer(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(ServiceLogo);
    }
}
