using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Endpoints;

// two read-only lists for the screens, facts requests and personal activity, neither changes anything
public static class ReviewSupportEndpoints
{
    private const int MaxRows = 100;
    private const int MaxDetailLength = 300;

    public static void MapReviewSupportEndpoints(this WebApplication app)
    {
        // every writer's note asking for facts, newest first, read only for the editor
        app.MapGet("/api/facts-requests", async (AppDbContext db) =>
        {
            var rows = await db.FactsRequests.AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .Take(MaxRows)
                .Select(r => new
                {
                    id = r.Id,
                    productId = r.ProductId,
                    productName = r.Product!.Name,
                    note = r.Note,
                    createdAt = r.CreatedAt,
                    requestedByUserId = r.RequestedByUserId,
                    requestedByEmail = db.UserDirectory.Where(u => u.UserId == r.RequestedByUserId).Select(u => u.Email).FirstOrDefault(),
                    hasFacts = r.Product!.Materials.Any() || r.Product.Certifications.Any() || r.Product.Origin != null,
                })
                .ToListAsync();

            return Results.Ok(rows);
        }).RequireAuthorization(AuthorizationSetup.PublishPolicy);

        // your own audit rows only, never the actual copy, works for either persona
        app.MapGet("/api/my/activity", async (AppDbContext db, HttpContext http) =>
        {
            var me = CurrentUser.GetId(http.User);
            if (me is null) return Results.Ok(Array.Empty<object>());

            var rows = await db.AuditLedger.AsNoTracking()
                .Where(e => e.UserId == me)
                .OrderByDescending(e => e.Id)
                .Take(MaxRows)
                .Select(e => new
                {
                    timestamp = e.Timestamp,
                    action = e.Action,
                    outcome = e.Outcome,
                    productId = e.ProductId,
                    productName = e.Product != null ? e.Product.Name : null,
                    justification = e.Justification,
                    detail = e.Detail,
                    market = e.Market,
                })
                .ToListAsync();

            // facts changes store before and after as JSON for the audit log, not shown in this list
            return Results.Ok(rows.Select(r => new
            {
                r.timestamp,
                r.action,
                r.outcome,
                r.productId,
                r.productName,
                r.justification,
                detail = r.action == Models.AuditActions.FactsChange || r.detail is null
                    ? null
                    : r.detail.Length <= MaxDetailLength ? r.detail : r.detail[..MaxDetailLength] + "…",
                r.market,
            }));
        }).RequireAuthorization();
    }
}
