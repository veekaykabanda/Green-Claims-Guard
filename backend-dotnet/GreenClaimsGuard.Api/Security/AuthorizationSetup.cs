using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace GreenClaimsGuard.Api.Security;

public static class AuthorizationSetup
{
    public const string PublishPolicy = "RequirePublishPermission";

    // overriding a block and editing facts each need their own permission, on top of being a senior editor
    public const string EditFactsPolicy = "RequireEditFactsPermission";

    // just means signed in, persona not required, only /api/me uses this so people with no access still get a response
    public const string AuthenticatedPolicy = "RequireSignedIn";

    // using auth0's own built in permissions claim because it's reliable, a custom claim from a post login action wasn't
    public const string PermissionsClaimType = "permissions";

    public const string AnalysePermission = "analyse:claims";
    public const string PublishPermission = "publish:product";
    public const string OverridePermission = "override:compliance";
    public const string EditFactsPermission = "edit:product-facts";

    public static IServiceCollection AddGreenClaimsAuthorization(this IServiceCollection services)
    {
        // persona is required as both the default and fallback policy, so a forgotten attribute can't leave an endpoint open and no role means no access, only /health and /api/me skip this
        var needsPersona = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context => Personas.HasPersona(context.User))
            .Build();

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(needsPersona)
            .SetFallbackPolicy(needsPersona)
            .AddPolicy(AuthenticatedPolicy, policy => policy.RequireAuthenticatedUser())
            // publishing, facts, the queue, the audit trail and data imports all use this one policy, they're the same trust level
            .AddPolicy(PublishPolicy, policy => policy.RequireClaim(PermissionsClaimType, PublishPermission))
            .AddPolicy(EditFactsPolicy, policy => policy
                .RequireClaim(PermissionsClaimType, PublishPermission)
                .RequireClaim(PermissionsClaimType, EditFactsPermission));

        // Who may open a product, and how a refusal is worded.
        services.AddSingleton<IAuthorizationHandler, ProductOwnerHandler>();
        services.AddScoped<ProductAccess>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationHandler>();

        return services;
    }
}
