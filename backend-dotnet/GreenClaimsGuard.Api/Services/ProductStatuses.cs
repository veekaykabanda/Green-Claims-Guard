using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

public static class ProductStatus
{
    public const string Draft = "Draft";
    public const string InReview = "InReview";
    public const string Published = "Published";

    // only shown on the submissions list, the product itself goes back to Draft after a withdrawal
    public const string Withdrawn = "Withdrawn";

    // sent back by the editor, you can edit it again like a draft but with a reason attached
    public const string SentBack = "SentBack";
}

// products don't store their own status, it's worked out from the latest review history entry so it can't get out of sync
public static class ProductStatuses
{
    public static string FromReview(string? overallStatus) => overallStatus switch
    {
        ComplianceStatus.ReadyToPublishSubjectToReview => ProductStatus.InReview,
        ComplianceStatus.Published => ProductStatus.Published,
        ComplianceStatus.SentBack => ProductStatus.SentBack,
        _ => ProductStatus.Draft,
    };

    // same as the product's status, but shows "Withdrawn" instead of "Draft" on your submissions list
    public static string FromSubmission(string? overallStatus) =>
        overallStatus == ComplianceStatus.Withdrawn ? ProductStatus.Withdrawn : FromReview(overallStatus);

    // copy is locked while it's in review or once it's published
    public static bool IsLocked(string status) => status is ProductStatus.InReview or ProductStatus.Published;

    // What to tell the writer when they try to change locked copy.
    public static string LockedMessage(string status) => status == ProductStatus.Published
        ? "This product is published, so its copy is locked."
        : "This product is in review, so its copy is locked. Withdraw it to make changes.";

    public static async Task<string> CurrentAsync(AppDbContext db, Guid productId)
    {
        var newest = await db.ComplianceReviews.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.Id)
            .Select(r => r.OverallStatus)
            .FirstOrDefaultAsync();
        return FromReview(newest);
    }

    // latest review step per product, the table only ever grows so the highest Id is the newest
    public static IQueryable<ComplianceReview> LatestPerProduct(AppDbContext db)
    {
        var latestIds = db.ComplianceReviews.GroupBy(r => r.ProductId).Select(g => g.Max(r => r.Id));
        return db.ComplianceReviews.Where(r => latestIds.Contains(r.Id));
    }

    // latest submission per product, ignoring any publish or withdraw steps that came after
    public static IQueryable<ComplianceReview> LatestSubmissionPerProduct(AppDbContext db)
    {
        var latestIds = db.ComplianceReviews
            .Where(r => r.OverallStatus == ComplianceStatus.ReadyToPublishSubjectToReview)
            .GroupBy(r => r.ProductId)
            .Select(g => g.Max(r => r.Id));
        return db.ComplianceReviews.Where(r => latestIds.Contains(r.Id));
    }
}
