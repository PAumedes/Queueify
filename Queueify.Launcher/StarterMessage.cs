namespace Queueify.Launcher;

/// <summary>What the starter says: its loading screen, and when it can't start the app. It has no string tables of its own.</summary>
public static class StarterMessage
{
    public static string For(Exception error, bool spanish) => error is BundleDamagedException
        ? (spanish ? "Esta copia de Queueify está dañada o incompleta. Descargala de nuevo."
                   : "This copy of Queueify is damaged or incomplete. Download it again.")
        : (spanish ? "Queueify no pudo arrancar." : "Queueify couldn't start.") + "\n\n" + error.Message;

    /// <summary>The line under the name on the loading screen.</summary>
    public static string Loading(bool unpacking, bool spanish) => unpacking
        ? (spanish ? "Preparando Queueify…" : "Getting Queueify ready…")
        : (spanish ? "Abriendo…" : "Starting…");

    /// <summary>
    /// How long to wait before showing the loading screen: at once when a new version unpacks (a few
    /// seconds), and only when it's slow otherwise (an unpacked version opens in under a second).
    /// </summary>
    public static TimeSpan LoadingDelay(bool unpacking) => unpacking ? TimeSpan.Zero : TimeSpan.FromMilliseconds(600);
}