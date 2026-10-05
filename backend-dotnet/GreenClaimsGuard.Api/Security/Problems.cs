using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;

namespace GreenClaimsGuard.Api.Security;

// standard replies for "who are you" and "you can't do that", they never mention what's being protected so a refusal doesn't leak whether a product exists
public static class Problems
{
    public const string SignInDetail = "Sign in to continue.";
    public const string ForbiddenDetail = "You do not have access to this. If you think you should, ask a Senior Editor.";
    public const string NotFoundDetail = "That item does not exist.";

    public static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed", detail: ForbiddenDetail);

    public static IResult NotFound() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", detail: NotFoundDetail);

    // request is fine but the item's in a state that blocks it (like locked copy or nothing to withdraw)
    public static IResult Conflict(string detail) => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not possible right now", detail: detail);
}

// makes a refused request (no token or not allowed) return the same plain error body as every other refusal, on top of the normal 401 / 403 handling
public class ProblemDetailsAuthorizationHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        await _default.HandleAsync(next, context, policy, authorizeResult);

        if ((authorizeResult.Challenged || authorizeResult.Forbidden) && !context.Response.HasStarted)
        {
            var challenged = authorizeResult.Challenged;
            var problem = new ProblemDetails
            {
                Status = challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden,
                Title = challenged ? "Sign in required" : "Not allowed",
                Detail = challenged ? Problems.SignInDetail : Problems.ForbiddenDetail,
            };
            context.Response.StatusCode = problem.Status.Value;
            await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }
}
