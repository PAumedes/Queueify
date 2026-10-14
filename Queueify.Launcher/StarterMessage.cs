namespace Queueify.Launcher;

/// <summary>What the starter shows when it can't start the app. It has no string tables of its own.</summary>
public static class StarterMessage
{
    public static string For(Exception error, bool spanish) => error is BundleDamagedException
        ? (spanish ? "Esta copia de Queueify está dañada o incompleta. Descargala de nuevo."
                   : "This copy of Queueify is damaged or incomplete. Download it again.")
        : (spanish ? "Queueify no pudo arrancar." : "Queueify couldn't start.") + "\n\n" + error.Message;
}
