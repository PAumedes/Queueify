namespace Queueify.Web.Services;

/// <summary>A one-line result shown under a control; errors get a mark as well as a colour.</summary>
public sealed record Notice(string Text, bool IsError = false);
