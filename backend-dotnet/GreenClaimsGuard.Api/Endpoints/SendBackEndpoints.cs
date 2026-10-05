using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Endpoints;

// editor sends a submission back to the writer with the reason it fell short
public static class SendBackEndpoints
{
    public const int MinCommentLength = 10;
    public const int MaxCommentLength = 1000;

    public static void MapSendBackEndpoints(this WebApplication app)
    {
        // sends copy back to editable, adds a new history step with its own audit row
        app.MapPost("/api/claims/send-back", async (
            SendBackRequest? request,
            AppDbContext db,
            ProductAccess access,
            ILogger<Program> logger,
            HttpContext http) =>
        {
            if (request is null || request.ProductId == Guid.Empty)
            {
                return Results.BadRequest(new { message = "productId is required." });
            }

            var reason = SendBackReasons.Normalise(request.ReasonCategory);
            if (reason is null)
            {
                return Results.BadRequest(new
                {
                    message = "Choose a reason: " + string.Join(", ", SendBackReasons.All.Select(r => r.Label)) + ".",
                });
            }

            var comment = (request.Comment ?? string.Empty).Trim();
            if (comment.Length < MinCommentLength)
            {
                return Results.BadRequest(new { message = $"Say what needs to change, in at least {MinCommentLength} characters." });
            }
            if (comment.Length > MaxCommentLength)
            {
                return Results.BadRequest(new { message = $"The comment must be {MaxCommentLength:N0} characters or fewer." });
            }

            var opened = await access.OpenAsync(request.ProductId, http.User);
            if (!opened.IsOk) return opened.Failure;

            var newest = await db.ComplianceReviews
                .Where(r => r.ProductId == request.ProductId)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
            if (newest is null || newest.OverallStatus != ComplianceStatus.ReadyToPublishSubjectToReview)
            {
                return Problems.Conflict("Only a submission that is waiting for sign-off can be sent back.");
            }

            var editor = CurrentUser.GetId(http.User);
            try
            {
                await DraftEndpoints.RestoreFromSubmissionAsync(db, newest);
                db.ComplianceReviews.Add(new ComplianceReview
                {
                    ProductId = request.ProductId,
                    ActorUserId = editor,
                    Market = newest.Market,
                    RulesVersion = newest.RulesVersion,
                    AiStatus = newest.AiStatus,
                    OpenIssueCount = newest.OpenIssueCount,
                    NeedsOverride = newest.NeedsOverride,
                    ProductName = newest.ProductName,
                    Category = newest.Category,
                    Subcategory = newest.Subcategory,
                    Tags = newest.Tags,
                    FinalDescription = newest.FinalDescription,
                    OverallStatus = ComplianceStatus.SentBack,
                    DecisionsJson = "[]",
                    SendBackReason = reason,
                    SendBackComment = comment,
                });
                db.AuditLedger.Add(new AuditEntry
                {
                    ProductId = request.ProductId,
                    Action = AuditActions.SendBack,
                    Outcome = AuditOutcomes.Ok,
                    CopySnapshot = newest.FinalDescription,
                    CopyHash = AuditEntryFactory.HashOf(newest.FinalDescription),
                    Market = newest.Market ?? Markets.Uk,
                    RulesStatus = EngineStatus.NotChecked,
                    AiStatus = newest.AiStatus ?? EngineStatus.NotChecked,
                    RulesVersion = newest.RulesVersion ?? string.Empty,
                    Justification = comment,
                    Detail = $"Sent back: {SendBackReasons.LabelFor(reason)}",
                    UserId = editor,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not send back the submission for product {ProductId}", request.ProductId);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Not saved",
                    detail: "The database is unavailable, so nothing was sent back.");
            }

            return Results.Ok(new { productId = request.ProductId, status = ProductStatus.SentBack });
        }).RequireAuthorization(AuthorizationSetup.PublishPolicy);
    }

    // what the writer sees about a send-back, the reason and the comment
    public static object? Describe(string? overallStatus, string? reason, string? comment, DateTime at) =>
        overallStatus == ComplianceStatus.SentBack
            ? new
            {
                reasonCategory = reason,
                reasonLabel = SendBackReasons.LabelFor(reason),
                comment,
                at,
            }
            : null;
}
