namespace Queueify.Web.Services;

/// <summary>
/// The address people reach this server at, from <c>Queueify:PublicBaseUrl</c>. Texts that leave the server (the connect
/// text a PC is given with a token) are built from it, never from the request's Host header, which a misconfigured
/// proxy would let anyone choose.
/// </summary>
public static class PublicUrl
{
    /// <summary>The configured origin (scheme, host, port), or null when none is set or it would not be safe to hand a token to.</summary>
    public static string? Of(IConfiguration config)
    {
        if (!Uri.TryCreate(config["Queueify:PublicBaseUrl"], UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0) return null;
        var secure = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        return secure ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
}
