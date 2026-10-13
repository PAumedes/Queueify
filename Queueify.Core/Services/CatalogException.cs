namespace Queueify.Services;

/// <summary>A catalog error with a message that can be shown to the user as-is.</summary>
public sealed class CatalogException(string message) : Exception(message);
