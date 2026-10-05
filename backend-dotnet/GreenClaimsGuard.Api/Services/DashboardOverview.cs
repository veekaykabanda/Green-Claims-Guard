using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

public class DashboardOverviewResponse
{
    public DateTime GeneratedAt { get; set; }

    // distinct products checked or submitted in the last 7 days
    public int ProductsCheckedThisWeek { get; set; }

    // critical issues still open in each product's latest state
    public int OpenCriticalIssues { get; set; }

    // submissions waiting on a senior editor where the AI check never finished
    public int ItemsPendingAiReview { get; set; }

    public int SubmissionsWaitingForPublish { get; set; }

    // publishes this month (UTC) that went ahead despite a blocking reason
    public int OverridesThisMonth { get; set; }

    // most flagged phrases over the last 30 days
    public List<FlaggedPhraseCount> MostFlaggedPhrases { get; set; } = new();
}

public class FlaggedPhraseCount
{
    public string Phrase { get; set; } = "";
    public int Count { get; set; }
}

// every number here comes straight from the audit ledger and review workflow, nothing is estimated
public static class DashboardOverview
{
    private const int TopPhrases = 5;
    private const int PhraseRowLimit = 2000;

    public static async Task<DashboardOverviewResponse> BuildAsync(AppDbContext db, DateTime nowUtc)
    {
        var weekAgo = nowUtc.AddDays(-7);
        var monthAgo = nowUtc.AddDays(-30);
        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var productsChecked = await db.AuditLedger
            .Where(e => e.ProductId != null && e.Timestamp >= weekAgo
                        && (e.Action == AuditActions.Check || e.Action == AuditActions.Submit))
            .Select(e => e.ProductId)
            .Distinct()
            .CountAsync();

        var overrides = await db.AuditLedger
            .CountAsync(e => e.Action == AuditActions.Override && e.Timestamp >= monthStart);

        // each product's latest step, from the append-only workflow table
        var latestReviewIds = db.ComplianceReviews.GroupBy(r => r.ProductId).Select(g => g.Max(r => r.Id));
        var pending = await db.ComplianceReviews
            .Where(r => latestReviewIds.Contains(r.Id) && r.OverallStatus == ComplianceStatus.ReadyToPublishSubjectToReview)
            .Select(r => r.AiStatus)
            .ToListAsync();

        // each product's latest ledger step, a check or blocked attempt leaves issues open, a successful submit or publish means they're dealt with
        var latestLedgerIds = db.AuditLedger
            .Where(e => e.ProductId != null)
            .GroupBy(e => e.ProductId)
            .Select(g => g.Max(e => e.Id));
        var latestStates = await db.AuditLedger
            .Where(e => latestLedgerIds.Contains(e.Id)
                        && (e.Action == AuditActions.Check || e.Outcome == AuditOutcomes.Blocked))
            .Select(e => e.IssuesJson)
            .ToListAsync();
        var openCritical = latestStates.Sum(json => LedgerIssues.Parse(json).Count(i => i.Critical && !i.Resolved));

        var recent = await db.AuditLedger
            .Where(e => e.Timestamp >= monthAgo
                        && (e.Action == AuditActions.Check || e.Action == AuditActions.Submit))
            .OrderByDescending(e => e.Id)
            .Take(PhraseRowLimit)
            .Select(e => e.IssuesJson)
            .ToListAsync();
        var phrases = recent
            .SelectMany(json => LedgerIssues.Parse(json).Select(i => i.Phrase).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
            .GroupBy(p => p!.Trim().ToLowerInvariant())
            .Select(g => new FlaggedPhraseCount { Phrase = g.Key, Count = g.Count() })
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.Phrase)
            .Take(TopPhrases)
            .ToList();

        return new DashboardOverviewResponse
        {
            GeneratedAt = nowUtc,
            ProductsCheckedThisWeek = productsChecked,
            OpenCriticalIssues = openCritical,
            ItemsPendingAiReview = pending.Count(status => status != EngineStatus.Ok),
            SubmissionsWaitingForPublish = pending.Count,
            OverridesThisMonth = overrides,
            MostFlaggedPhrases = phrases
        };
    }
}
