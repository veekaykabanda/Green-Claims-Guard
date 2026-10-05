using System.Text.Json;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Endpoints;

// only shows your own stuff (or everyone's if you're the editor)
public static class CopywriterEndpoints
{
    private const int MaxRows = 200;

    public static void MapCopywriterEndpoints(this WebApplication app)
    {
        // your own products, or everyone's if you're the editor
        app.MapGet("/api/products", async (string? search, int? take, AppDbContext db, HttpContext http) =>
        {
            var me = CurrentUser.GetId(http.User);
            var isSeniorEditor = Personas.Of(http.User) == Personas.SeniorEditor;

            IQueryable<Product> products = db.Products.AsNoTracking();
            if (!isSeniorEditor)
            {
                products = products.Where(p => p.CreatedByUserId == me && me != null);
            }
            var term = (search ?? string.Empty).Trim();
            if (term.Length > 0)
            {
                products = products.Where(p => p.Name.Contains(term));
            }

            var rows = await products
                .OrderByDescending(p => p.CreatedAt)
                .Take(Math.Clamp(take ?? 25, 1, 100))
                .Select(p => new { p.Id, p.Name, p.CreatedAt })
                .ToListAsync();

            var ids = rows.Select(r => r.Id).ToList();
            var latest = await ProductStatuses.LatestPerProduct(db)
                .Where(r => ids.Contains(r.ProductId))
                .Select(r => new { r.ProductId, r.OverallStatus })
                .ToListAsync();
            var statusById = latest.ToDictionary(r => r.ProductId, r => ProductStatuses.FromReview(r.OverallStatus));

            return Results.Ok(rows.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                status = statusById.GetValueOrDefault(r.Id, ProductStatus.Draft),
                createdAt = r.CreatedAt,
            }));
        }).RequireAuthorization();

        // only shows stuff you created and submitted yourself
        app.MapGet("/api/my/submissions", async (AppDbContext db, HttpContext http) =>
        {
            var me = CurrentUser.GetId(http.User);
            if (me is null) return Results.Ok(Array.Empty<object>());

            var mine = await db.Products.AsNoTracking()
                .Where(p => p.CreatedByUserId == me)
                .Select(p => new { p.Id, p.Name })
                .ToListAsync();
            var ids = mine.Select(p => p.Id).ToList();

            var latest = await ProductStatuses.LatestPerProduct(db)
                .Where(r => ids.Contains(r.ProductId))
                .Select(r => new { r.ProductId, r.OverallStatus, r.CreatedAt, r.SendBackReason, r.SendBackComment })
                .ToListAsync();
            var submissions = await ProductStatuses.LatestSubmissionPerProduct(db)
                .Where(r => ids.Contains(r.ProductId))
                .Select(r => new { r.ProductId, r.Market, r.AiStatus, r.OpenIssueCount, r.NeedsOverride, r.CreatedAt })
                .ToListAsync();

            var nameById = mine.ToDictionary(p => p.Id, p => p.Name);
            var latestById = latest.ToDictionary(r => r.ProductId);

            var rows = submissions
                .Select(s => new
                {
                    productId = s.ProductId,
                    productName = nameById[s.ProductId],
                    market = s.Market ?? Markets.Uk,
                    submittedAt = s.CreatedAt,
                    openIssueCount = s.OpenIssueCount ?? 0,
                    needsOverride = s.NeedsOverride,
                    aiStatus = s.AiStatus ?? EngineStatus.NotChecked,
                    status = ProductStatuses.FromSubmission(latestById[s.ProductId].OverallStatus),
                    updatedAt = latestById[s.ProductId].CreatedAt,
                    sendBack = SendBackEndpoints.Describe(
                        latestById[s.ProductId].OverallStatus, latestById[s.ProductId].SendBackReason,
                        latestById[s.ProductId].SendBackComment, latestById[s.ProductId].CreatedAt),
                })
                .OrderByDescending(r => r.updatedAt)
                .Take(MaxRows);

            return Results.Ok(rows);
        }).RequireAuthorization();

        // every submitted version for the owner or the editor, full audit trail is editor only
        app.MapGet("/api/products/{id:guid}/history", async (Guid id, AppDbContext db, ProductAccess access, HttpContext http) =>
        {
            var opened = await access.OpenAsync(id, http.User);
            if (!opened.IsOk) return opened.Failure;

            var steps = await db.ComplianceReviews.AsNoTracking()
                .Where(r => r.ProductId == id)
                .OrderBy(r => r.Id)
                .ToListAsync();

            var versions = new List<object>();
            var number = 0;
            foreach (var step in steps.Where(s => s.OverallStatus == ComplianceStatus.ReadyToPublishSubjectToReview))
            {
                number++;
                var nextSubmission = steps.FirstOrDefault(s =>
                    s.OverallStatus == ComplianceStatus.ReadyToPublishSubjectToReview && s.Id > step.Id);
                // what happened to this version before the next submission
                var outcome = steps.FirstOrDefault(s =>
                    s.Id > step.Id
                    && (nextSubmission is null || s.Id < nextSubmission.Id)
                    && (s.OverallStatus == ComplianceStatus.Published || s.OverallStatus == ComplianceStatus.Withdrawn
                        || s.OverallStatus == ComplianceStatus.SentBack));

                versions.Add(new
                {
                    version = number,
                    submittedAt = step.CreatedAt,
                    productName = step.ProductName,
                    needsOverride = step.NeedsOverride,
                    market = step.Market ?? Markets.Uk,
                    aiStatus = step.AiStatus ?? EngineStatus.NotChecked,
                    openIssueCount = step.OpenIssueCount ?? 0,
                    text = step.FinalDescription,
                    decisions = DecisionsOf(step),
                    publishedAt = outcome?.OverallStatus == ComplianceStatus.Published ? outcome.CreatedAt : (DateTime?)null,
                    withdrawnAt = outcome?.OverallStatus == ComplianceStatus.Withdrawn ? outcome.CreatedAt : (DateTime?)null,
                    sendBack = outcome is null ? null : SendBackEndpoints.Describe(outcome.OverallStatus, outcome.SendBackReason, outcome.SendBackComment, outcome.CreatedAt),
                });
            }

            var latest = steps.LastOrDefault();
            return Results.Ok(new
            {
                productId = id,
                productName = opened.Product!.Name,
                status = ProductStatuses.FromReview(latest?.OverallStatus),
                versions,
            });
        }).RequireAuthorization();

        // pulls your own submission back before it's signed off
        app.MapPost("/api/claims/withdraw", async (
            WithdrawRequest? request,
            AppDbContext db,
            ProductAccess access,
            ILogger<Program> logger,
            HttpContext http) =>
        {
            if (request is null || request.ProductId == Guid.Empty)
            {
                return Results.BadRequest(new { message = "productId is required." });
            }

            var opened = await access.OpenOwnedAsync(request.ProductId, http.User);
            if (!opened.IsOk) return opened.Failure;

            var me = CurrentUser.GetId(http.User)!;

            var newest = await db.ComplianceReviews
                .Where(r => r.ProductId == request.ProductId)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
            if (newest is null || newest.OverallStatus != ComplianceStatus.ReadyToPublishSubjectToReview)
            {
                return Problems.Conflict("Only a submission that is waiting for sign-off can be withdrawn.");
            }

            try
            {
                // withdraw doesn't delete anything, just adds a new entry
                await DraftEndpoints.RestoreFromSubmissionAsync(db, newest);
                db.ComplianceReviews.Add(new ComplianceReview
                {
                    ProductId = request.ProductId,
                    ActorUserId = me,
                    Market = newest.Market,
                    RulesVersion = newest.RulesVersion,
                    AiStatus = newest.AiStatus,
                    OpenIssueCount = newest.OpenIssueCount,
                    FinalDescription = newest.FinalDescription,
                    OverallStatus = ComplianceStatus.Withdrawn,
                    DecisionsJson = "[]",
                });
                db.AuditLedger.Add(new AuditEntry
                {
                    ProductId = request.ProductId,
                    Action = AuditActions.Withdraw,
                    Outcome = AuditOutcomes.Ok,
                    CopySnapshot = newest.FinalDescription,
                    CopyHash = AuditEntryFactory.HashOf(newest.FinalDescription),
                    Market = newest.Market ?? Markets.Uk,
                    RulesStatus = EngineStatus.NotChecked,
                    AiStatus = newest.AiStatus ?? EngineStatus.NotChecked,
                    RulesVersion = newest.RulesVersion ?? string.Empty,
                    Detail = "The writer withdrew this submission before sign-off.",
                    UserId = me,
                });
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not withdraw the submission for product {ProductId}", request.ProductId);
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Not saved",
                    detail: "The database is unavailable, so nothing was withdrawn.");
            }

            return Results.Ok(new { productId = request.ProductId, status = ProductStatus.Draft });
        }).RequireAuthorization();
    }

    // what the writer decided for each issue, applied the rewrite or kept it with a reason
    private static IEnumerable<object> DecisionsOf(ComplianceReview step)
    {
        List<IssueDecisionInput> decisions;
        try
        {
            decisions = JsonSerializer.Deserialize<List<IssueDecisionInput>>(step.DecisionsJson) ?? new();
        }
        catch (JsonException)
        {
            decisions = new();
        }

        return decisions
            .Where(d => d.UserDecision == ComplianceDecision.AppliedSuggestion
                        || d.UserDecision == ComplianceDecision.KeptOriginalWithJustification)
            .Select(d => (object)new
            {
                issueId = d.IssueId,
                phrase = d.Phrase,
                category = d.Category,
                critical = string.Equals(d.Severity, "high", StringComparison.OrdinalIgnoreCase),
                decision = d.UserDecision == ComplianceDecision.AppliedSuggestion ? "Applied" : "Kept",
                reason = d.UserJustification,
            })
            .ToList();
    }
}
