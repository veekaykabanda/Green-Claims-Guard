using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

[Collection(ApiCollection.Name)]
public class AppliedRewriteLedgerTests
{
    private readonly ApiTestFactory _factory;

    public AppliedRewriteLedgerTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnAppliedRewrite_IsRecordedOnTheLedger_EvenThoughTheFlaggedWordingIsGone()
    {
        var copywriter = _factory.CreateCopywriter("auth0|writer-42");
        var created = await copywriter.PostAsJsonAsync("/api/products", new { name = "Rewrite recorded" });
        var product = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var submit = await copywriter.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = "A plain cotton shirt with a relaxed fit.",
            issueDecisions = new[]
            {
                new { issueId = "bamboo-fabric-misleading-01", severity = "HIGH", userDecision = ComplianceDecision.AppliedSuggestion }
            }
        });
        Assert.True((await submit.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        var entries = await _factory.WithDbAsync(db => db.AuditLedger
            .Where(e => e.ProductId == product)
            .OrderBy(e => e.Id)
            .ToListAsync());

        var applied = Assert.Single(entries, e => e.Action == AuditActions.Apply);
        Assert.Equal("auth0|writer-42", applied.UserId);
        Assert.Contains("bamboo-fabric-misleading-01", applied.Detail);
        Assert.Contains("client", applied.Detail);
        Assert.Contains(entries, e => e.Action == AuditActions.Submit);
    }
}
