using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Queueify;

/// <summary>
/// Album covers for {x:Bind}. {Binding} turned the URL into an image by itself, but it finds properties
/// by reflection, which the trimmed app doesn't keep.
/// </summary>
public static class Covers
{
    public static ImageSource? From(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? new BitmapImage(uri) : null;
}
