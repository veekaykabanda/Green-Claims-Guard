using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

public class MyOverviewResponse
{
    public DateTime GeneratedAt { get; set; }

    // products the caller has saved but not submitted yet (includes ones sent back to them)
    public int Drafts { get; set; }
    public int AwaitingSignOff { get; set; }
    public int SentBack { get; set; }
    public int Published { get; set; }

    // phrases that got flagged most in the caller's own checks over the last 30 days
    public List<MyFlaggedPhrase> TopPhrases { get; set; } = new();
}

public class MyFlaggedPhrase
{
    public string Phrase { get; set; } = "";
    public int Count { get; set; }

    // category the phrase is usually flagged under, so a tip can link to the right guidance page
    public string? Category { get; set; }
}

// this is all about one person's own work, no company-wide numbers on purpose
public static class MyOverview
{
    private const int TopPhrases = 5;
    private const int RowLimit = 500;

    public static async Task<MyOverviewResponse> BuildAsync(AppDbContext db, string me, DateTime nowUtc)
    {
        var monthAgo = nowUtc.AddDays(-30);

        var myProducts = await db.Products.AsNoTracking()
            .Where(p => p.CreatedByUserId == me)
            .Select(p => p.Id)
            .ToListAsync();

        var drafts = await db.ProductDrafts.CountAsync(d => myProducts.Contains(d.ProductId));

        var latest = await ProductStatuses.LatestPerProduct(db)
            .Where(r => myProducts.Contains(r.ProductId))
            .Select(r => r.OverallStatus)
            .ToListAsync();

        var rows = await db.AuditLedger.AsNoTracking()
            .Where(e => e.UserId == me
                        && e.Timestamp >= monthAgo
                        && (e.Action == AuditActions.Check || e.Action == AuditActions.Submit))
            .OrderByDescending(e => e.Id)
            .Take(RowLimit)
            .Select(e => e.IssuesJson)
            .ToListAsync();

        // Each check counts a phrase once, however many times it appears in that check.
        var flagged = rows
            .SelectMany(json => LedgerIssues.Parse(json)
                .Where(i => !string.IsNullOrWhiteSpace(i.Phrase))
                .GroupBy(i => i.Phrase!.Trim().ToLowerInvariant())
                .Select(g => (Phrase: g.Key, Category: g.Select(i => i.Category).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)))))
            .GroupBy(x => x.Phrase)
            .Select(g => new MyFlaggedPhrase
            {
                Phrase = g.Key,
                Count = g.Count(),
                Category = g.Select(x => x.Category).Where(c => c is not null)
                    .GroupBy(c => c).OrderByDescending(c => c.Count()).Select(c => c.Key).FirstOrDefault(),
            })
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Phrase)
            .Take(TopPhrases)
            .ToList();

        return new MyOverviewResponse
        {
            GeneratedAt = nowUtc,
            Drafts = drafts,
            AwaitingSignOff = latest.Count(s => s == ComplianceStatus.ReadyToPublishSubjectToReview),
            SentBack = latest.Count(s => s == ComplianceStatus.SentBack),
            Published = latest.Count(s => s == ComplianceStatus.Published),
            TopPhrases = flagged,
        };
    }
}
