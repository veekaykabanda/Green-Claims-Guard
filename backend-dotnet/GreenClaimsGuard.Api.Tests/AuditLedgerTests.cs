using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuditLedgerTests
{
    private const string CleanText = "A plain wool jumper with ribbed cuffs.";
    private const string CriticalText = "Our new range is made from sustainable bamboo fabric.";
    private const string MediumOnlyText = "Made with recycled polyester yarn.";

    private readonly ApiTestFactory _factory;

    public AuditLedgerTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid productId, string text, object[]? decisions = null) =>
        client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId,
            finalDescription = text,
            issueDecisions = decisions ?? Array.Empty<object>()
        });

    private Task<List<AuditEntry>> LedgerFor(Guid productId) =>
        _factory.WithDbAsync(db => db.AuditLedger.AsNoTracking().Where(e => e.ProductId == productId).OrderBy(e => e.Id).ToListAsync());

    [Fact]
    public async Task AManualCheck_IsRecorded_WithTheServersViewOfTheText()
    {
        var user = "auth0|checker-1";
        var client = _factory.CreateCopywriter(user);
        var product = await CreateProductAsync(client, "Checked product");

        var response = await client.PostAsJsonAsync("/api/analyze", new
        {
            text = $"  {CriticalText}  ",
            rulesOnly = true,
            trigger = "manual",
            productId = product
        });
        response.EnsureSuccessStatusCode();

        var entry = Assert.Single(await LedgerFor(product));
        Assert.Equal(AuditActions.Check, entry.Action);
        Assert.Equal(AuditOutcomes.Ok, entry.Outcome);
        Assert.Equal(user, entry.UserId);
        Assert.Equal("UK", entry.Market);
        Assert.StartsWith("UK-", entry.RulesVersion);
        Assert.Equal(EngineStatus.Ok, entry.RulesStatus);
        Assert.Equal(EngineStatus.Skipped, entry.AiStatus);
        Assert.Equal(CriticalText, entry.CopySnapshot);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CriticalText))).ToLowerInvariant(), entry.CopyHash);
        Assert.Contains("\"Severity\":\"high\"", entry.IssuesJson);
    }

    [Fact]
    public async Task ALiveTypingCheck_WritesNothingToTheLedger()
    {
        var client = _factory.CreateCopywriter();
        var product = await CreateProductAsync(client, "Live only");

        (await client.PostAsJsonAsync("/api/analyze", new { text = CriticalText, rulesOnly = true, productId = product }))
            .EnsureSuccessStatusCode();

        Assert.Empty(await LedgerFor(product));
    }

    [Fact]
    public async Task ABlockedSubmit_IsRecordedWithTheReasons()
    {
        var client = _factory.CreateCopywriter("auth0|blocked-submitter");
        var product = await CreateProductAsync(client, "Blocked submit");

        await SubmitAsync(client, product, CriticalText);

        var entry = Assert.Single(await LedgerFor(product));
        Assert.Equal(AuditActions.Submit, entry.Action);
        Assert.Equal(AuditOutcomes.Blocked, entry.Outcome);
        Assert.Equal("auth0|blocked-submitter", entry.UserId);
        Assert.Contains("Critical issue still in the text", entry.Detail);
        Assert.Equal(CriticalText, entry.CopySnapshot);
    }

    [Fact]
    public async Task ASuccessfulSubmit_RecordsEachDecisionAndTheSubmission_WithTheJustification()
    {
        var client = _factory.CreateCopywriter("auth0|deciding-copywriter");
        var product = await CreateProductAsync(client, "Decided submit");

        var analysis = await (await client.PostAsJsonAsync("/api/analyze", new { text = MediumOnlyText, rulesOnly = true }))
            .Content.ReadFromJsonAsync<AnalyzeResponse>();
        var decisions = analysis!.GroupedFindings.Select(f => (object)new
        {
            issueId = f.IssueId,
            severity = f.Severity.ToUpperInvariant(),
            userDecision = ComplianceDecision.KeptOriginalWithJustification,
            userJustification = "Supplier GRS certificate on file, ref GRS-2026-114.",
            requiresEvidence = f.RequiresEvidence
        }).ToArray();
        Assert.NotEmpty(decisions);

        var response = await SubmitAsync(client, product, MediumOnlyText, decisions);
        Assert.True((await response.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        var ledger = await LedgerFor(product);
        var keeps = ledger.Where(e => e.Action == AuditActions.Keep).ToList();
        Assert.Equal(decisions.Length, keeps.Count);
        Assert.All(keeps, k => Assert.Equal("Supplier GRS certificate on file, ref GRS-2026-114.", k.Justification));
        Assert.All(keeps, k => Assert.Equal("auth0|deciding-copywriter", k.UserId));

        var submit = Assert.Single(ledger, e => e.Action == AuditActions.Submit);
        Assert.Equal(AuditOutcomes.Ok, submit.Outcome);
        Assert.True(keeps.Max(k => k.Id) < submit.Id, "decisions are recorded before the submission that carries them");
    }

    [Fact]
    public async Task ABlockedPublish_ThenAnOverride_AreBothRecorded_WithTheSeniorEditorsReason()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor("auth0|overriding-editor");
        var product = await CreateProductAsync(copywriter, "Override trail");
        await SubmitAsync(copywriter, product, CleanText);

        using (_factory.Llm.Using(EngineStatus.Failed))
        {
            var blocked = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

            var overridden = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new
            {
                productId = product,
                overrideReason = "OpenAI outage on launch day; copy checked by hand against the supplier sheet."
            });
            Assert.Equal(HttpStatusCode.OK, overridden.StatusCode);
        }

        var ledger = await LedgerFor(product);
        var blockedPublish = Assert.Single(ledger, e => e.Action == AuditActions.Publish && e.Outcome == AuditOutcomes.Blocked);
        Assert.Contains("AI check", blockedPublish.Detail);
        Assert.Equal(EngineStatus.Failed, blockedPublish.AiStatus);

        var overrideRow = Assert.Single(ledger, e => e.Action == AuditActions.Override);
        Assert.Equal("OpenAI outage on launch day; copy checked by hand against the supplier sheet.", overrideRow.Justification);
        Assert.Equal("auth0|overriding-editor", overrideRow.UserId);
        Assert.Contains("AI check", overrideRow.Detail);

        var publishOk = Assert.Single(ledger, e => e.Action == AuditActions.Publish && e.Outcome == AuditOutcomes.Ok);
        Assert.True(overrideRow.Id < publishOk.Id);
    }

    [Fact]
    public async Task ACleanPublish_IsRecordedWithoutAnOverrideEntry()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "Plain publish trail");
        await SubmitAsync(copywriter, product, CleanText);

        (await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product })).EnsureSuccessStatusCode();

        var ledger = await LedgerFor(product);
        Assert.Contains(ledger, e => e.Action == AuditActions.Publish && e.Outcome == AuditOutcomes.Ok);
        Assert.DoesNotContain(ledger, e => e.Action == AuditActions.Override);
    }

    [Fact]
    public async Task TheLedgerIsInsertOnly_EvenForDirectSql()
    {
        var copywriter = _factory.CreateCopywriter();
        var product = await CreateProductAsync(copywriter, "Immutable trail");
        await SubmitAsync(copywriter, product, CleanText);
        var id = (await LedgerFor(product)).First().Id;

        var update = await Assert.ThrowsAnyAsync<Exception>(() => _factory.WithDbAsync(async db =>
            await db.Database.ExecuteSqlRawAsync("UPDATE audit_ledger SET outcome = 'Tampered' WHERE id = {0}", id)));
        Assert.Contains("insert-only", update.ToString());

        var delete = await Assert.ThrowsAnyAsync<Exception>(() => _factory.WithDbAsync(async db =>
            await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_ledger WHERE id = {0}", id)));
        Assert.Contains("insert-only", delete.ToString());

        var stillThere = (await LedgerFor(product)).First(e => e.Id == id);
        Assert.NotEqual("Tampered", stillThere.Outcome);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task TheApiOffersNoWayToChangeTheLedger(string method)
    {
        var response = await _factory.CreateSeniorEditor().SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/audit/1"));

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task OnlyASeniorEditorCanReadTheLedger()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateAnonymousClient().GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter().GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter().GetAsync("/api/audit/export")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateSeniorEditor().GetAsync("/api/audit")).StatusCode);
    }

    [Fact]
    public async Task TheLedgerCanBeFilteredByProduct_NewestFirst()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        var mine = await CreateProductAsync(copywriter, "Filtered A");
        var other = await CreateProductAsync(copywriter, "Filtered B");
        await SubmitAsync(copywriter, mine, CleanText);
        await SubmitAsync(copywriter, other, CleanText);
        (await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = mine })).EnsureSuccessStatusCode();

        var rows = await seniorEditor.GetFromJsonAsync<List<AuditEntryDto>>($"/api/audit?productId={mine}");

        Assert.All(rows!, r => Assert.Equal(mine, r.ProductId));
        Assert.All(rows!, r => Assert.Equal("Filtered A", r.ProductName));
        Assert.Equal(new[] { AuditActions.Publish, AuditActions.Submit }, rows!.Select(r => r.Action).ToArray());
        Assert.True(rows![0].Id > rows[1].Id);
    }

    [Fact]
    public async Task TheCsvExport_HasAHeader_EscapesQuotes_AndNeutralisesFormulas()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "Csv \"quoted\" product");

        var analysis = await (await copywriter.PostAsJsonAsync("/api/analyze", new { text = MediumOnlyText, rulesOnly = true }))
            .Content.ReadFromJsonAsync<AnalyzeResponse>();
        var decisions = analysis!.GroupedFindings.Select(f => (object)new
        {
            issueId = f.IssueId,
            severity = f.Severity.ToUpperInvariant(),
            userDecision = ComplianceDecision.KeptOriginalWithJustification,
            userJustification = "=HYPERLINK(\"http://evil.example\",\"click\")",
            requiresEvidence = f.RequiresEvidence
        }).ToArray();
        await SubmitAsync(copywriter, product, MediumOnlyText, decisions);

        var response = await seniorEditor.GetAsync($"/api/audit/export?productId={product}");
        var csv = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString());
        Assert.StartsWith("﻿\"Timestamp (UTC)\",\"Product\"", csv);
        Assert.Contains("\"Csv \"\"quoted\"\" product\"", csv);
        // a cell starting with = gets written as text so a spreadsheet won't run it
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.DoesNotContain(",\"=HYPERLINK(", csv);
    }
}
