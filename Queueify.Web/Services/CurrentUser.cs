using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Queueify.Web.Services;

public static class CurrentUser
{
    /// <summary>The signed-in user's id (their data folder), or null when nobody is signed in.</summary>
    public static async Task<string?> IdAsync(this AuthenticationStateProvider auth)
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        return user.Identity?.IsAuthenticated == true && user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;
    }
}
