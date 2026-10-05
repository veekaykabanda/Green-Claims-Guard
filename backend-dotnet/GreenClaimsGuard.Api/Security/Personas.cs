using System.Security.Claims;

namespace GreenClaimsGuard.Api.Security;

// persona comes from the permissions on the auth0 token, never a role name, so the server and the screen always agree, no permission means no persona and everything's blocked except /api/me
public static class Personas
{
    public const string Copywriter = "copywriter";
    public const string SeniorEditor = "senior-editor";
    public const string None = "none";

    public static string Of(ClaimsPrincipal? user)
    {
        if (user is null) return None;
        if (Has(user, AuthorizationSetup.PublishPermission)) return SeniorEditor;
        if (Has(user, AuthorizationSetup.AnalysePermission)) return Copywriter;
        return None;
    }

    public static bool HasPersona(ClaimsPrincipal? user) => Of(user) != None;

    public static string LabelOf(string persona) => persona switch
    {
        SeniorEditor => "Senior Editor",
        Copywriter => "Copywriter",
        _ => "No access",
    };

    private static bool Has(ClaimsPrincipal user, string permission) =>
        user.HasClaim(AuthorizationSetup.PermissionsClaimType, permission);
}
