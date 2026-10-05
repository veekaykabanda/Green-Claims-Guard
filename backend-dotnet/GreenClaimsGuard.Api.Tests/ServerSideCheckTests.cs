using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// submit and publish always check the text on the server, never trust what the client says it found
[Collection(ApiCollection.Name)]
public class ServerSideCheckTests
{
    private const string CriticalText = "Our new range is made from sustainable bamboo fabric.";
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string MediumOnlyText = "Made with recycled polyester yarn.";

    private readonly ApiTestFactory _factory;

    public ServerSideCheckTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<AnalyzeResponse> AnalyzeAsync(HttpClient client, string text)
    {
        var response = await client.PostAsJsonAsync("/api/analyze", new { text, rulesOnly = true });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AnalyzeResponse>())!;
    }

    private static async Task<MarkReadyResponse> SubmitAsync(HttpClient client, Guid productId, string text, object[]? decisions = null)
    {
        var response = await client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId,
            finalDescription = text,
            issueDecisions = decisions ?? Array.Empty<object>()
        });
        Assert.True(response.IsSuccessStatusCode, $"mark-ready returned {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<MarkReadyResponse>())!;
    }

    private static object[] DecisionsFor(AnalyzeResponse analysis, string decision, string? justification = null) =>
        analysis.GroupedFindings.Select(f => (object)new
        {
            issueId = f.IssueId,
            severity = f.Severity.ToUpperInvariant(),
            userDecision = decision,
            userJustification = justification,
            requiresEvidence = f.RequiresEvidence
        }).ToArray();

    private async Task<List<PendingReviewItem>> PendingAsync() =>
        (await _factory.CreateSeniorEditor().GetFromJsonAsync<List<PendingReviewItem>>("/api/claims/pending-review"))!;

    [Fact]
    public async Task LiveAnalyze_ReportsWhatRanAndWhatTheServerWouldAllow()
    {
        var result = await AnalyzeAsync(_factory.CreateCopywriter(), CleanText);

        Assert.Equal(EngineStatus.Ok, result.RulesStatus);
        Assert.Equal(EngineStatus.Skipped, result.AiStatus);
        Assert.Equal("UK", result.Market);
        Assert.StartsWith("UK-", result.RulesVersion);
        Assert.True(result.SubmitAllowed);
        Assert.False(result.PublishAllowed);
        Assert.Contains(result.BlockingReasons, r => r.Contains("AI check has not run"));
    }

    [Fact]
    public async Task Submit_IgnoresTheClientsDecisions_ForACriticalIssueStillInTheText()
    {
        var copywriter = _factory.CreateCopywriter();
        var product = await CreateProductAsync(copywriter, "Critical still present");

        var analysis = await AnalyzeAsync(copywriter, CriticalText);
        Assert.Contains(analysis.GroupedFindings, f => f.Severity == "high");

        // client says it applied a rewrite for every issue, but the text hasn't actually changed
        var result = await SubmitAsync(copywriter, product, CriticalText, DecisionsFor(analysis, ComplianceDecision.AppliedSuggestion));

        Assert.Equal(ComplianceStatus.ChangesRequired, result.OverallStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("Critical issue still in the text"));
        Assert.DoesNotContain(await PendingAsync(), p => p.ProductId == product);
    }

    [Fact]
    public async Task Submit_WithNoDecisionsAtAll_IsBlockedWhenTheTextHasIssues()
    {
        var copywriter = _factory.CreateCopywriter();
        var product = await CreateProductAsync(copywriter, "No decisions");

        var result = await SubmitAsync(copywriter, product, CriticalText);

        Assert.False(result.ReadyToPublishSubjectToReview);
        Assert.NotEmpty(result.ValidationErrors);
        Assert.DoesNotContain(await PendingAsync(), p => p.ProductId == product);
    }

    [Fact]
    public async Task Submit_CleanText_IsRecordedWithWhoDidItAndWhatWasChecked()
    {
        var copywriter = _factory.CreateCopywriter("auth0|copywriter-77");
        var product = await CreateProductAsync(copywriter, "Plain submission");

        var result = await SubmitAsync(copywriter, product, CleanText);

        Assert.True(result.ReadyToPublishSubjectToReview);
        Assert.Equal(EngineStatus.Ok, result.AiStatus);

        var item = Assert.Single(await PendingAsync(), p => p.ProductId == product);
        Assert.Equal("auth0|copywriter-77", item.SubmittedByUserId);
        Assert.Equal("UK", item.Market);
        Assert.Equal(EngineStatus.Ok, item.AiStatus);
        Assert.Equal(0, item.OpenIssueCount);
    }

    [Fact]
    public async Task Submit_AMediumIssue_NeedsAValidDecision()
    {
        var copywriter = _factory.CreateCopywriter();
        var analysis = await AnalyzeAsync(copywriter, MediumOnlyText);
        Assert.NotEmpty(analysis.GroupedFindings);
        Assert.DoesNotContain(analysis.GroupedFindings, f => f.Severity == "high");
        Assert.DoesNotContain(analysis.GroupedFindings, f => f.RequiresEvidence);

        var withoutDecision = await CreateProductAsync(copywriter, "Medium, no decision");
        Assert.False((await SubmitAsync(copywriter, withoutDecision, MediumOnlyText)).ReadyToPublishSubjectToReview);

        var keptNoReason = await CreateProductAsync(copywriter, "Medium, kept without a reason");
        Assert.False((await SubmitAsync(copywriter, keptNoReason, MediumOnlyText,
            DecisionsFor(analysis, ComplianceDecision.KeptOriginalWithJustification))).ReadyToPublishSubjectToReview);

        var keptWithReason = await CreateProductAsync(copywriter, "Medium, kept with a reason");
        Assert.True((await SubmitAsync(copywriter, keptWithReason, MediumOnlyText,
            DecisionsFor(analysis, ComplianceDecision.KeptOriginalWithJustification, "Supplier certificate on file, ref GRS-2026-114."))).ReadyToPublishSubjectToReview);
    }

    [Fact]
    public async Task Submit_WhenTheAiIsDown_IsStillAllowed_ButTheGapIsRecorded()
    {
        var copywriter = _factory.CreateCopywriter();
        var product = await CreateProductAsync(copywriter, "AI down at submit");

        using (_factory.Llm.Using(EngineStatus.NotConfigured))
        {
            var result = await SubmitAsync(copywriter, product, CleanText);

            Assert.True(result.ReadyToPublishSubjectToReview);
            Assert.Equal(EngineStatus.NotConfigured, result.AiStatus);
            Assert.Contains("re-run", result.Message);
        }

        var item = Assert.Single(await PendingAsync(), p => p.ProductId == product);
        Assert.Equal(EngineStatus.NotConfigured, item.AiStatus);
    }

    [Fact]
    public async Task Publish_ChecksTheStoredTextAgain_SoATamperedSubmissionIsBlocked()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "Tampered after submit");
        await SubmitAsync(copywriter, product, CleanText);

        // Someone changes the stored text after it passed submission.
        await _factory.WithDbAsync(async db =>
        {
            var row = await db.ComplianceReviews.Where(r => r.ProductId == product).OrderByDescending(r => r.Id).FirstAsync();
            row.FinalDescription = CriticalText;
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        var body = await response.Content.ReadFromJsonAsync<PublishResponse>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(body!.Success);
        Assert.Contains(body.BlockingReasons, r => r.Contains("Critical issue still in the text"));
        Assert.Contains(await PendingAsync(), p => p.ProductId == product);
    }

    [Fact]
    public async Task Publish_IsBlockedWhenTheAiHasNotRun_UntilASeniorEditorOverridesWithAWrittenReason()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor("auth0|editor-9");
        var product = await CreateProductAsync(copywriter, "Needs an override");
        await SubmitAsync(copywriter, product, CleanText);

        using (_factory.Llm.Using(EngineStatus.NotConfigured))
        {
            var blocked = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
            var blockedBody = await blocked.Content.ReadFromJsonAsync<PublishResponse>();
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains(blockedBody!.BlockingReasons, r => r.Contains("AI check"));

            var tooShort = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product, overrideReason = "ok" });
            Assert.Equal(HttpStatusCode.Conflict, tooShort.StatusCode);
            Assert.Contains("written reason", (await tooShort.Content.ReadFromJsonAsync<PublishResponse>())!.Message);

            var overridden = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new
            {
                productId = product,
                overrideReason = "OpenAI outage during the launch window; copy reviewed by hand."
            });
            var overriddenBody = await overridden.Content.ReadFromJsonAsync<PublishResponse>();
            Assert.Equal(HttpStatusCode.OK, overridden.StatusCode);
            Assert.True(overriddenBody!.Success);
            Assert.True(overriddenBody.Overridden);
        }

        var published = await _factory.WithDbAsync(db => db.ComplianceReviews
            .Where(r => r.ProductId == product && r.OverallStatus == ComplianceStatus.Published)
            .SingleAsync());
        Assert.Equal("auth0|editor-9", published.ActorUserId);
        Assert.Equal("OpenAI outage during the launch window; copy reviewed by hand.", published.OverrideReason);
        Assert.Equal(EngineStatus.NotConfigured, published.AiStatus);
        Assert.DoesNotContain(await PendingAsync(), p => p.ProductId == product);
    }

    [Fact]
    public async Task Publish_WhenEverythingPasses_NeedsNoOverride_AndRecordsNone()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor("auth0|editor-3");
        var product = await CreateProductAsync(copywriter, "Plain all the way");
        await SubmitAsync(copywriter, product, CleanText);

        var response = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        var body = await response.Content.ReadFromJsonAsync<PublishResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body!.Overridden);

        var published = await _factory.WithDbAsync(db => db.ComplianceReviews
            .Where(r => r.ProductId == product && r.OverallStatus == ComplianceStatus.Published)
            .SingleAsync());
        Assert.Null(published.OverrideReason);
        Assert.Equal("auth0|editor-3", published.ActorUserId);
        Assert.Equal(EngineStatus.Ok, published.AiStatus);
    }

    [Fact]
    public async Task Publish_OfSomethingNeverSubmitted_IsRejected()
    {
        var product = await CreateProductAsync(_factory.CreateCopywriter(), "Never submitted");

        var response = await _factory.CreateSeniorEditor().PostAsJsonAsync("/api/claims/publish", new { productId = product });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
