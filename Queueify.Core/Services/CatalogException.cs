namespace Queueify.Services;

/// <summary>
/// A problem worth showing as is. <see cref="KeysRejected"/> is set only when the service refused the
/// Client ID or secret, so Settings can tell "check your keys" apart from a busy or broken server.
/// </summary>
public sealed class CatalogException(string message, bool keysRejected = false) : Exception(message)
{
    public bool KeysRejected { get; } = keysRejected;
}
