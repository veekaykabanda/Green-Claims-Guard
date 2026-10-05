using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Endpoints;

// only the creator can see a draft, not even the editor, and saving one never touches the audit log
public static class DraftEndpoints
{
    public const int MaxTextLength = 5000;
    private const int PreviewLength = 140;

    // turns the submission back into a draft, but won't overwrite a newer draft that's already there
    public static async Task RestoreFromSubmissionAsync(AppDbContext db, ComplianceReview submission)
    {
        if (await db.ProductDrafts.AnyAsync(d => d.ProductId == submission.ProductId)) return;

        db.ProductDrafts.Add(new ProductDraft
        {
            ProductId = submission.ProductId,
            Text = submission.FinalDescription,
            Market = submission.Market ?? Markets.Uk,
            SavedAt = DateTime.UtcNow,
        });
    }

    public static void MapDraftEndpoints(this WebApplication app)
    {
        // saves or replaces the draft, empty text means no draft
        app.MapPut("/api/products/{id:guid}/draft", async (
            Guid id, SaveDraftRequest? request, AppDbContext db, ProductAccess access, HttpContext http) =>
        {
            if (request?.Text is null)
            {
                return Results.BadRequest(new { message = "Request must include the draft text." });
            }
            if (request.Text.Length > MaxTextLength)
            {
                return Results.BadRequest(new { message = $"A draft must be {MaxTextLength:N0} characters or fewer." });
            }
            if (!Markets.TryNormalise(request.Market, out var market))
            {
                return Results.BadRequest(new { message = "Market must be UK or EU." });
            }

            var opened = await access.OpenOwnedAsync(id, http.User);
            if (!opened.IsOk) return opened.Failure;

            var current = await ProductStatuses.CurrentAsync(db, id);
            if (ProductStatuses.IsLocked(current)) return Problems.Conflict(ProductStatuses.LockedMessage(current));

            var draft = await db.ProductDrafts.FindAsync(id);

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                if (draft is not null)
                {
                    db.ProductDrafts.Remove(draft);
                    await db.SaveChangesAsync();
                }
                return Results.Ok(new { saved = false });
            }

            if (draft is null)
            {
                draft = new ProductDraft { ProductId = id };
                db.ProductDrafts.Add(draft);
            }
            draft.Text = request.Text;
            draft.Market = market;
            draft.SavedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { saved = true, savedAt = draft.SavedAt });
        }).RequireAuthorization();

        // GET /api/products/{id}/draft
        app.MapGet("/api/products/{id:guid}/draft", async (Guid id, AppDbContext db, ProductAccess access, HttpContext http) =>
        {
            var opened = await access.OpenOwnedAsync(id, http.User);
            if (!opened.IsOk) return opened.Failure;

            var draft = await db.ProductDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.ProductId == id);
            return draft is null
                ? Problems.NotFound()
                : Results.Ok(new
                {
                    productId = id,
                    productName = opened.Product!.Name,
                    text = draft.Text,
                    market = draft.Market,
                    savedAt = draft.SavedAt,
                });
        }).RequireAuthorization();

        // deleting nothing is fine, this is safe to call even without a draft
        app.MapDelete("/api/products/{id:guid}/draft", async (Guid id, AppDbContext db, ProductAccess access, HttpContext http) =>
        {
            var opened = await access.OpenOwnedAsync(id, http.User);
            if (!opened.IsOk) return opened.Failure;

            var draft = await db.ProductDrafts.FindAsync(id);
            if (draft is not null)
            {
                db.ProductDrafts.Remove(draft);
                await db.SaveChangesAsync();
            }
            return Results.NoContent();
        }).RequireAuthorization();

        // your own drafts, newest first
        app.MapGet("/api/my/drafts", async (AppDbContext db, HttpContext http) =>
        {
            var me = CurrentUser.GetId(http.User);
            if (me is null) return Results.Ok(Array.Empty<object>());

            var drafts = await db.ProductDrafts.AsNoTracking()
                .Where(d => db.Products.Any(p => p.Id == d.ProductId && p.CreatedByUserId == me))
                .OrderByDescending(d => d.SavedAt)
                .Take(200)
                .Select(d => new
                {
                    productId = d.ProductId,
                    productName = db.Products.Where(p => p.Id == d.ProductId).Select(p => p.Name).First(),
                    market = d.Market,
                    savedAt = d.SavedAt,
                    text = d.Text,
                })
                .ToListAsync();

            return Results.Ok(drafts.Select(d => new
            {
                d.productId,
                d.productName,
                d.market,
                d.savedAt,
                preview = d.text.Length <= PreviewLength ? d.text : d.text[..PreviewLength] + "…",
            }));
        }).RequireAuthorization();
    }
}
