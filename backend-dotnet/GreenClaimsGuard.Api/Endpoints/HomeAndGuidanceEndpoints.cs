using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using GreenClaimsGuard.Api.Services;

namespace GreenClaimsGuard.Api.Endpoints;

// what feeds the writer's home page and guidance page
public static class HomeAndGuidanceEndpoints
{
    public static void MapHomeAndGuidanceEndpoints(this WebApplication app)
    {
        // your own counts and most flagged phrases, nothing company wide
        app.MapGet("/api/my/overview", async (AppDbContext db, IDbService dbService, ILogger<Program> logger, HttpContext http) =>
        {
            var me = CurrentUser.GetId(http.User);
            if (me is null) return Problems.Forbidden();

            if (!dbService.IsConfigured)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Not available",
                    detail: "The database is not configured, so there is nothing to count.");
            }

            try
            {
                return Results.Ok(await MyOverview.BuildAsync(db, me, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not build the overview for a writer");
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Not available",
                    detail: "The database is unavailable, so your overview cannot be shown.");
            }
        }).RequireAuthorization();

        // the rules people are checked against, in plain words, open to both personas
        app.MapGet("/api/guidance", (string? market, IRuleEngineService rules) =>
        {
            if (!Markets.TryNormalise(market, out var normalised))
            {
                return Results.BadRequest(new { message = "Market must be UK or EU." });
            }
            return Results.Ok(Guidance.Build(rules, normalised));
        }).RequireAuthorization();
    }
}
