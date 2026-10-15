namespace Queueify.Services;

/// <summary>
/// A sync problem with a message the user can act on. Messages never carry the server's answer, the
/// address or the token, so showing or logging one can't leak the connection.
/// </summary>
public sealed class SyncException(string message) : Exception(message);
