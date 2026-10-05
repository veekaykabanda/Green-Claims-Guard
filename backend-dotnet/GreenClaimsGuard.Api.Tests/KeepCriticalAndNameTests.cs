using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// writer can keep a critical issue if they give a reason, but only a Senior Editor can publish it after, with their own reason
[Collection(ApiCollection.Name)]
public class KeepCriticalAndNameTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string CriticalText = "Our new range is made from sustainable bamboo fabric.";
    private const string GoodReason = "Supplier certificate on file, ref GRS-2026-114.";

    private readonly ApiTestFactory _factory;

    public KeepCriticalAndNameTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static string NewUser() => $"auth0|writer-{Guid.NewGuid():N}";

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<GroupedFinding>> FindingsAsync(
        HttpClient client, string text, Guid? product = null, string? productName = null, string market = "UK",
        string? productCategory = null, string? productSubcategory = null, string? productTags = null)
    {
        var response = await client.PostAsJsonAsync("/api/analyze", new
        {
            text, rulesOnly = true, productId = product, productName, market,
            productCategory, productSubcategory, productTags
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AnalyzeResponse>())!.GroupedFindings;
    }

    private static object[] Keep(IEnumerable<GroupedFinding> findings, string reason) =>
        findings.Select(f => (object)new
        {
            issueId = f.IssueId,
            severity = f.Severity.ToUpperInvariant(),
            userDecision = ComplianceDecision.KeptOriginalWithJustification,
            userJustification = reason,
            requiresEvidence = f.RequiresEvidence,
            resolvedSentenceSignature = f.SentenceSignature
        }).ToArray();

    private static async Task<MarkReadyResponse> SubmitAsync(
        HttpClient client, Guid product, string text, object[]? decisions = null,
        string? category = null, string? subcategory = null, string? tags = null)
    {
        var response = await client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = text,
            issueDecisions = decisions ?? Array.Empty<object>(),
            category,
            subcategory,
            tags
        });
        Assert.True(response.IsSuccessStatusCode, $"mark-ready returned {(int)response.StatusCode}");
        return (await response.Content.ReadFromJsonAsync<MarkReadyResponse>())!;
    }

    // Keeping a critical issue

    [Fact]
    public async Task ACriticalIssue_CanBeKeptWithAReason_SoSubmissionPasses_ButItNeedsAnOverride()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Kept critical product");
        var findings = await FindingsAsync(writer, CriticalText);
        Assert.Contains(findings, f => f.Severity == "high");

        var result = await SubmitAsync(writer, product, CriticalText, Keep(findings, GoodReason));

        Assert.True(result.ReadyToPublishSubjectToReview);
        Assert.True(result.NeedsOverride);
        Assert.Contains("Senior Editor must override", result.Message);

        var pending = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/claims/pending-review");
        var item = pending.EnumerateArray().Single(i => i.GetProperty("productId").GetGuid() == product);
        Assert.True(item.GetProperty("needsOverride").GetBoolean());

        var mine = (await writer.GetFromJsonAsync<JsonElement>("/api/my/submissions")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.True(mine.GetProperty("needsOverride").GetBoolean());
    }

    [Fact]
    public async Task AReasonUnder15Characters_IsNotEnoughToKeepACriticalIssue()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Short reason product");
        var findings = await FindingsAsync(writer, CriticalText);

        var result = await SubmitAsync(writer, product, CriticalText, Keep(findings, "too short here")); // 14 characters

        Assert.False(result.ReadyToPublishSubjectToReview);
        Assert.Contains(result.ValidationErrors, e => e.Contains("at least 15 characters"));

        var enough = await SubmitAsync(writer, product, CriticalText, Keep(findings, "exactly fifteen!")); // 15 characters
        Assert.True(enough.ReadyToPublishSubjectToReview);
    }

    [Fact]
    public async Task ASeniorEditorMustOverride_ToPublishAKeptCriticalIssue_WithTheirOwnReason()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor("auth0|overriding-editor-4");
        var product = await CreateProductAsync(writer, "Override needed product");
        await SubmitAsync(writer, product, CriticalText, Keep(await FindingsAsync(writer, CriticalText), GoodReason));

        // writer can never publish or override their own kept issue
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync("/api/claims/publish", new { productId = product, overrideReason = "I say it is fine, publish it." })).StatusCode);

        var blocked = await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        var blockedBody = await blocked.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains(blockedBody!.BlockingReasons, r => r.Contains("Kept by the writer") && r.Contains("override"));

        var published = await editor.PostAsJsonAsync("/api/claims/publish", new
        {
            productId = product,
            overrideReason = "Certificate checked against the supplier file today."
        });
        var publishedBody = await published.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.True(publishedBody!.Overridden);

        var ledger = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product).ToListAsync());
        var overrideRow = Assert.Single(ledger, e => e.Action == AuditActions.Override);
        Assert.Equal("auth0|overriding-editor-4", overrideRow.UserId);
        Assert.Equal("Certificate checked against the supplier file today.", overrideRow.Justification);
    }

    [Fact]
    public async Task WhatTheVerifiedFactsContradict_CanNeverBeKept()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Facts mismatch product");
        await editor.PutAsJsonAsync($"/api/products/{product}/facts", new
        {
            materials = new object[] { new { material = "cotton", percentage = 60 }, new { material = "polyester", percentage = 40 } },
            certifications = Array.Empty<string>(),
            reason = "Verified against the supplier composition sheet."
        });
        const string wrong = "A relaxed shirt in 70% cotton.";
        var findings = await FindingsAsync(writer, wrong, product);
        Assert.Contains(findings, f => f.Category == "product_facts_mismatch");

        var result = await SubmitAsync(writer, product, wrong, Keep(findings, GoodReason));

        Assert.False(result.ReadyToPublishSubjectToReview);
        Assert.Contains(result.ValidationErrors, e => e.Contains("cannot be kept"));
    }

    [Fact]
    public async Task ACriticalIssueStillInTheText_IsNotClearedByClaimingARewriteWasApplied()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Claimed rewrite product");
        var findings = await FindingsAsync(writer, CriticalText);
        var claimedApplied = findings.Select(f => (object)new
        {
            issueId = f.IssueId, severity = "HIGH", userDecision = ComplianceDecision.AppliedSuggestion,
            resolvedSentenceSignature = f.SentenceSignature
        }).ToArray();

        var result = await SubmitAsync(writer, product, CriticalText, claimedApplied);

        Assert.False(result.ReadyToPublishSubjectToReview);
        Assert.Contains(result.ValidationErrors, e => e.Contains("Critical issue still in the text"));
    }

    // The record of what was decided

    [Fact]
    public async Task TheSubmission_SnapshotsTheTextMarketRulesAiAndEveryDecision_OneAuditRowEach()
    {
        var writer = _factory.CreateCopywriter("auth0|snapshot-writer");
        var product = await CreateProductAsync(writer, "Snapshot product");
        var findings = await FindingsAsync(writer, CriticalText, market: "EU");
        var decisions = Keep(findings, GoodReason)
            .Append(new
            {
                issueId = "old-rewrite-01", severity = "MEDIUM", userDecision = ComplianceDecision.AppliedSuggestion,
                resolvedSentenceSignature = "a sentence that no longer exists"
            }).ToArray();

        var submit = await writer.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product, finalDescription = CriticalText, market = "EU", issueDecisions = decisions
        });
        Assert.True((await submit.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        var review = await _factory.WithDbAsync(db => db.ComplianceReviews.SingleAsync(r => r.ProductId == product));
        Assert.Equal(CriticalText, review.FinalDescription);
        Assert.Equal("EU", review.Market);
        Assert.StartsWith("EU-", review.RulesVersion);
        Assert.Equal("Ok", review.AiStatus);
        Assert.Equal("Snapshot product", review.ProductName);
        Assert.True(review.NeedsOverride);

        var entries = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product).ToListAsync());
        var keeps = entries.Where(e => e.Action == AuditActions.Keep).ToList();
        Assert.Equal(findings.Count, keeps.Count);
        Assert.All(keeps, k =>
        {
            Assert.Equal(GoodReason, k.Justification);
            Assert.Equal("auth0|snapshot-writer", k.UserId);
            Assert.Equal("EU", k.Market);
            Assert.Equal(CriticalText, k.CopySnapshot);
        });
        Assert.Contains(keeps, k => k.Detail!.Contains("needs a Senior Editor override"));
        Assert.Single(entries, e => e.Action == AuditActions.Apply);
        var submitRow = Assert.Single(entries, e => e.Action == AuditActions.Submit);
        Assert.Contains("Product name: Snapshot product", submitRow.Detail);
        Assert.Equal("Ok", submitRow.AiStatus);
    }

    [Fact]
    public async Task TheStoredDecisions_NameTheWordingAndCategory_FromTheServersOwnCheck()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Decision detail product");
        var findings = await FindingsAsync(writer, CriticalText);
        // client tries to claim the decision was about something else
        var decisions = findings.Select(f => (object)new
        {
            issueId = f.IssueId, severity = "LOW", userDecision = ComplianceDecision.KeptOriginalWithJustification,
            userJustification = GoodReason, resolvedSentenceSignature = f.SentenceSignature,
            phrase = "totally different words", category = "made_up"
        }).ToArray();
        await SubmitAsync(writer, product, CriticalText, decisions);

        var history = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/history");
        var version = Assert.Single(history.GetProperty("versions").EnumerateArray());
        var decision = version.GetProperty("decisions").EnumerateArray().First();

        Assert.NotEqual("totally different words", decision.GetProperty("phrase").GetString());
        Assert.NotEqual("made_up", decision.GetProperty("category").GetString());
        Assert.Contains(findings[0].MatchedPatterns[0], decision.GetProperty("phrase").GetString());
        Assert.True(decision.GetProperty("critical").GetBoolean());
        Assert.Equal(GoodReason, decision.GetProperty("reason").GetString());
        Assert.True(version.GetProperty("needsOverride").GetBoolean());
        Assert.Equal("Decision detail product", version.GetProperty("productName").GetString());
    }

    // What is submitted is the saved draft

    [Fact]
    public async Task Submitting_UsesTheSavedDraft_NotTheTextSentWithTheRequest()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Draft wins product");
        await writer.PutAsJsonAsync($"/api/products/{product}/draft", new { text = CleanText, market = "UK" });

        var response = await writer.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = CriticalText, // would get refused if this text actually counted
            issueDecisions = Array.Empty<object>()
        });
        var body = await response.Content.ReadFromJsonAsync<MarkReadyResponse>();

        Assert.True(body!.ReadyToPublishSubjectToReview);
        Assert.True(body.UsedDraft);
        var review = await _factory.WithDbAsync(db => db.ComplianceReviews.SingleAsync(r => r.ProductId == product));
        Assert.Equal(CleanText, review.FinalDescription);
    }

    [Fact]
    public async Task WithNoDraft_TheTextSentWithTheRequestIsUsed_AsBefore()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Plain draftless product");

        var body = await SubmitAsync(writer, product, CleanText);

        Assert.True(body.ReadyToPublishSubjectToReview);
        Assert.False(body.UsedDraft);
    }

    [Fact]
    public async Task ASeniorEditorSubmittingSomeoneElsesProduct_NeverReadsTheWritersPrivateDraft()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Private draft product");
        await writer.PutAsJsonAsync($"/api/products/{product}/draft", new { text = "The writer's private, unfinished thoughts.", market = "UK" });

        var body = await SubmitAsync(editor, product, CleanText);

        Assert.False(body.UsedDraft);
        var review = await _factory.WithDbAsync(db => db.ComplianceReviews.SingleAsync(r => r.ProductId == product));
        Assert.Equal(CleanText, review.FinalDescription);
    }

    // The name is checked like the description

    [Fact]
    public async Task AClaimInTheProductName_IsFlaggedLikeOneInTheDescription()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        var findings = await FindingsAsync(writer, CleanText, productName: "Carbon Neutral Linen Tee");

        var nameIssue = Assert.Single(findings, f => f.Field == "name");
        Assert.StartsWith("name-", nameIssue.IssueId);
        Assert.Equal("high", nameIssue.Severity);
        Assert.Equal("", nameIssue.SuggestionText);
        Assert.Contains("carbon neutral", nameIssue.MatchedPatterns, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(findings, f => f.Field == "description" && f.Severity == "high");
    }

    [Fact]
    public async Task ACheckWithNoProductName_ReportsNothingAboutTheName()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        Assert.DoesNotContain(await FindingsAsync(writer, CleanText), f => f.Field == "name");
    }

    [Fact]
    public async Task ACriticalClaimInTheNameBlocksSubmission_UntilItIsRenamed()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Carbon Neutral Linen Tee");

        var blocked = await SubmitAsync(writer, product, CleanText);
        Assert.False(blocked.ReadyToPublishSubjectToReview);
        Assert.Contains(blocked.ValidationErrors, e => e.Contains("Critical issue still in the product name"));

        Assert.Equal(HttpStatusCode.OK, (await writer.PatchAsJsonAsync($"/api/products/{product}", new { name = "Linen Tee" })).StatusCode);
        Assert.True((await SubmitAsync(writer, product, CleanText)).ReadyToPublishSubjectToReview);
    }

    [Fact]
    public async Task ANameClaimThatIsNotCritical_NeedsADecision_LikeAnyOther()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        const string name = "Pure Linen Tee";
        var product = await CreateProductAsync(writer, name);

        var blocked = await SubmitAsync(writer, product, CleanText);
        Assert.False(blocked.ReadyToPublishSubjectToReview);
        Assert.Contains(blocked.ValidationErrors, e => e.Contains("Needs a decision"));

        var nameFindings = (await FindingsAsync(writer, CleanText, product, name)).Where(f => f.Field == "name").ToList();
        Assert.NotEmpty(nameFindings);
        Assert.True((await SubmitAsync(writer, product, CleanText, Keep(nameFindings, "The name is the established brand line."))).ReadyToPublishSubjectToReview);
    }

    [Fact]
    public async Task TheNameIsCheckedAgainstTheVerifiedFacts_LikeTheDescription()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Facts name product");
        await editor.PutAsJsonAsync($"/api/products/{product}/facts", new
        {
            materials = new object[] { new { material = "cotton", percentage = 60 }, new { material = "polyester", percentage = 40 } },
            certifications = Array.Empty<string>(),
            reason = "Verified against the supplier composition sheet."
        });

        var findings = await FindingsAsync(writer, CleanText, product, "70% Cotton Tee");

        var issue = Assert.Single(findings, f => f.Category == "product_facts_mismatch");
        Assert.Equal("name", issue.Field);
        Assert.Equal("name-facts-mismatch-01", issue.IssueId);
    }

    [Fact]
    public async Task AnOverlongName_IsRefusedByACheck()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        var response = await writer.PostAsJsonAsync("/api/analyze", new { text = CleanText, rulesOnly = true, productName = new string('n', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void TheMinimumReasonForKeepingACriticalIssue_Is15Characters()
    {
        Assert.Equal(15, ComplianceOrchestrator.MinCriticalKeepReason);
    }

    // The Product Details card (category, subcategory, tags) is checked too

    [Fact]
    public async Task AClaimInTheCategory_IsFlaggedLikeOneInTheDescription()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        var findings = await FindingsAsync(writer, CleanText, productCategory: "Carbon Neutral Dresses");

        var issue = Assert.Single(findings, f => f.Field == "category");
        Assert.StartsWith("category-", issue.IssueId);
        Assert.Equal("high", issue.Severity);
        Assert.Equal("", issue.SuggestionText);
        Assert.Contains("carbon neutral", issue.MatchedPatterns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AClaimInTheSubcategory_IsFlaggedLikeOneInTheDescription()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        var findings = await FindingsAsync(writer, CleanText, productSubcategory: "Sustainable Midi Dresses");

        var issue = Assert.Single(findings, f => f.Field == "subcategory");
        Assert.StartsWith("subcategory-", issue.IssueId);
    }

    [Fact]
    public async Task AClaimInTheTags_IsFlaggedLikeOneInTheDescription()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        var findings = await FindingsAsync(writer, CleanText, productTags: "Linen, Eco-friendly, Summer");

        var issue = Assert.Single(findings, f => f.Field == "tags");
        Assert.StartsWith("tags-", issue.IssueId);
    }

    [Fact]
    public async Task ACheckWithNoProductDetails_ReportsNothingAboutThem()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        var findings = await FindingsAsync(writer, CleanText);

        Assert.DoesNotContain(findings, f => f.Field is "category" or "subcategory" or "tags");
    }

    [Fact]
    public async Task ACriticalClaimInTheCategory_BlocksSubmission()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Plain dress product");

        var blocked = await SubmitAsync(writer, product, CleanText, category: "Carbon Neutral Dresses");
        Assert.False(blocked.ReadyToPublishSubjectToReview);
        Assert.Contains(blocked.ValidationErrors, e => e.Contains("Critical issue still in the product category"));

        Assert.True((await SubmitAsync(writer, product, CleanText, category: "Dresses")).ReadyToPublishSubjectToReview);
    }

    [Fact]
    public async Task TheCategoryIsCheckedAgainstTheVerifiedFacts_LikeTheDescription()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Facts category product");
        await editor.PutAsJsonAsync($"/api/products/{product}/facts", new
        {
            materials = new object[] { new { material = "cotton", percentage = 60 }, new { material = "polyester", percentage = 40 } },
            certifications = Array.Empty<string>(),
            reason = "Verified against the supplier composition sheet."
        });

        var findings = await FindingsAsync(writer, CleanText, product, productCategory: "70% Cotton Dresses");

        var issue = Assert.Single(findings, f => f.Category == "product_facts_mismatch" && f.Field == "category");
        Assert.Equal("category-facts-mismatch-01", issue.IssueId);
    }

    [Fact]
    public async Task ASeniorEditorPublishingASubmission_ReChecksTheSubmittedCategory_NotWhateverIsCurrent()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Republish category product");

        // submitted with a clean category so it reaches sign-off
        var submitted = await SubmitAsync(writer, product, CleanText, category: "Dresses");
        Assert.True(submitted.ReadyToPublishSubjectToReview);

        var publishResponse = await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var publishBody = await publishResponse.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.True(publishBody!.Success);
    }
}
