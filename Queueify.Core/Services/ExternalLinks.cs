using System.Diagnostics;
using Queueify.Models;
using Windows.System;

namespace Queueify.Services;

/// <summary>Opens albums in the installed Spotify / Tidal app, falling back to the web player.</summary>
public static class ExternalLinks
{
    public static Task OpenSpotifyAsync(string id) => OpenAsync(PlayLinks.For(MusicService.Spotify, id));

    public static Task OpenTidalAsync(string id) => OpenAsync(PlayLinks.For(MusicService.Tidal, id));

    private static async Task OpenAsync(PlayLink link)
    {
        var app = new Uri(link.App);
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
        Process.Start(new ProcessStartInfo(new Uri(link.Web).AbsoluteUri) { UseShellExecute = true });
    }
}
