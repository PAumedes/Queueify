using System.Diagnostics;
using Windows.System;

namespace Queueify.Services;

/// <summary>Opens albums in the installed Spotify / Tidal app, falling back to the web player.</summary>
public static class ExternalLinks
{
    public static Task OpenSpotifyAsync(string id) =>
        OpenAsync(new Uri($"spotify:album:{id}"), new Uri($"https://open.spotify.com/album/{id}"));

    public static Task OpenTidalAsync(string id) =>
        OpenAsync(new Uri($"tidal://album/{id}"), new Uri($"https://tidal.com/album/{id}"));

    private static async Task OpenAsync(Uri app, Uri web)
    {
        try
        {
            var support = await Launcher.QueryUriSupportAsync(app, LaunchQuerySupportType.Uri);
            if (support == LaunchQuerySupportStatus.Available && await Launcher.LaunchUriAsync(app))
                return;
        }
        catch (Exception)
        {
            // Launcher can be unavailable in some unpackaged contexts; the browser fallback still works.
        }
        Process.Start(new ProcessStartInfo(web.AbsoluteUri) { UseShellExecute = true });
    }
}
