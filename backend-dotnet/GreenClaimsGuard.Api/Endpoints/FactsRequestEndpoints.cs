using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Endpoints;

// just a note to the editor, doesn't change any verified facts
public static class FactsRequestEndpoints
{
    public const int MinNoteLength = 5;
    public const int MaxNoteLength = 500;

    // enough to ask again if the first note wasn't clear, not enough to spam the editor's inbox
    public const int MaxPerProductPerDay = 5;

    public static void MapFactsRequestEndpoints(this WebApplication app)
    {
        // only the product's creator can ask
        app.MapPost("/api/products/{id:guid}/facts-requests", async (
            Guid id, RequestFactsRequest? request, AppDbContext db, ProductAccess access, HttpContext http) =>
        {
            var note = (request?.Note ?? string.Empty).Trim();
            if (note.Length < MinNoteLength)
            {
                return Results.BadRequest(new { message = $"Say what is missing, in at least {MinNoteLength} characters." });
            }
            if (note.Length > MaxNoteLength)
            {
                return Results.BadRequest(new { message = $"The note must be {MaxNoteLength} characters or fewer." });
            }

            var opened = await access.OpenOwnedAsync(id, http.User);
            if (!opened.IsOk) return opened.Failure;

            var me = CurrentUser.GetId(http.User)!;
            var since = DateTime.UtcNow.AddDays(-1);
            var recent = await db.FactsRequests.CountAsync(r => r.ProductId == id && r.RequestedByUserId == me && r.CreatedAt >= since);
            if (recent >= MaxPerProductPerDay)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Too many requests",
                    detail: "You have already asked for facts on this product several times today. A Senior Editor has your notes.");
            }

            var created = new FactsRequest { ProductId = id, RequestedByUserId = me, Note = note };
            db.FactsRequests.Add(created);
            await db.SaveChangesAsync();

            return Results.Created($"/api/products/{id}/facts-requests", new { id = created.Id, note = created.Note, createdAt = created.CreatedAt });
        }).RequireAuthorization();

        // newest first, only the editor sees who asked
        app.MapGet("/api/products/{id:guid}/facts-requests", async (Guid id, AppDbContext db, ProductAccess access, HttpContext http) =>
        {
            var opened = await access.OpenAsync(id, http.User);
            if (!opened.IsOk) return opened.Failure;

            var showWho = Personas.Of(http.User) == Personas.SeniorEditor;
            var rows = await db.FactsRequests.AsNoTracking()
                .Where(r => r.ProductId == id)
                .OrderByDescending(r => r.CreatedAt)
                .Take(50)
                .ToListAsync();

            return Results.Ok(rows.Select(r => new
            {
                id = r.Id,
                note = r.Note,
                createdAt = r.CreatedAt,
                requestedByUserId = showWho ? r.RequestedByUserId : null,
            }));
        }).RequireAuthorization();
    }
}
