using System.Security.Claims;

namespace GreenClaimsGuard.Api.Security;

public static class CurrentUser
{
    // auth0 only lets a login action add a claim under a custom name, so this is the name our post login action uses for email
    public const string EmailClaimType = "https://greenclaims-api/email";

    // jwt handler maps auth0's "sub" claim to nameidentifier by default, so check both just in case
    public static string? GetId(ClaimsPrincipal user) =>
        user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    // email from the login token, just for display, null if it's missing broken or too long
    public static string? GetEmail(ClaimsPrincipal user)
    {
        var email = user.FindFirstValue(EmailClaimType)?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 320 || !email.Contains('@')) return null;
        return email;
    }
}
