using System.Security.Claims;
using System.Text.Encodings.Web;
using GreenClaimsGuard.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GreenClaimsGuard.Api.Tests;

// fake auth for tests, since we can't mint real Auth0 tokens the caller just says who they are via headers, no X-Test-User header means anonymous
public class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";
    public const string EmailHeader = "X-Test-Email";

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var userId) || string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // matches the real JWT handler, keeps "sub" as "sub" so per-user stuff like rate limiting works the same as in production
        var claims = new List<Claim> { new("sub", userId.ToString()) };

        if (Request.Headers.TryGetValue(EmailHeader, out var email) && !string.IsNullOrWhiteSpace(email))
        {
            claims.Add(new Claim(CurrentUser.EmailClaimType, email.ToString()));
        }

        if (Request.Headers.TryGetValue(PermissionsHeader, out var permissions))
        {
            foreach (var permission in permissions.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim(AuthorizationSetup.PermissionsClaimType, permission));
            }
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, nameType: "sub", roleType: ClaimTypes.Role));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
