using System.Diagnostics;
using Windows.System;

namespace Queueify.Services;

/// <summary>
/// Opens albums in the installed Spotify / Tidal app, falling back to the web player. It takes a
/// <see cref="PlayLink"/>, which <see cref="PlayLinks.Only"/> builds only from a plain id: ids can arrive from a sync
/// server, and nothing else may reach the OS protocol launcher.
/// </summary>
public static class ExternalLinks
{
    public static async Task OpenAsync(PlayLink link)
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
