namespace Queueify.Services;

/// <summary>
/// Which album cover addresses the app loads. They arrive from the services and from other PCs through
/// sync, so only web addresses: a file or network path could read local files or leak the Windows login
/// to another machine.
/// </summary>
public static class CoverUrls
{
    public static Uri? Loadable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri : null;
}
