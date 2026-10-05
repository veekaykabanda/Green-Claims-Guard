namespace GreenClaimsGuard.Api.Security;

// checks that must pass before the app starts, fails fast with a clear message instead of running broken
public static class StartupChecks
{
    public static void EnsureSafeToStart(IHostEnvironment environment, string? auth0Domain, string? auth0Audience)
    {
        // no Auth0 settings in production means every request gets refused and looks like a broken app, so just refuse to start instead
        if (environment.IsProduction()
            && (string.IsNullOrWhiteSpace(auth0Domain) || string.IsNullOrWhiteSpace(auth0Audience)))
        {
            throw new InvalidOperationException(
                "Refusing to start in Production without Auth0. Set AUTH0_DOMAIN and AUTH0_AUDIENCE.");
        }
    }
}