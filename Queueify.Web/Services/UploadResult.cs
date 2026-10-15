namespace Queueify.Web.Services;

/// <summary>What the browser got back from an upload endpoint: its HTTP status (0 when the request never completed) and body text.</summary>
public sealed record UploadResult(int Status, string Body);
