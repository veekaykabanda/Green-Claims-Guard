using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// editor can't sign off their own submission unless they're the only one (then it's flagged), overriding and editing facts need their own permissions, and it all gets logged
[Collection(ApiCollection.Name)]
public class SeniorEditorRulesTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string CriticalText = "Our new range is made from sustainable bamboo fabric.";
    private const string GoodReason = "Supplier certificate on file, ref GRS-2026-114.";

    private readonly ApiTestFactory _factory;

    public SeniorEditorRulesTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static string NewUser() => $"auth0|user-{Guid.NewGuid():N}";

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task SubmitCleanAsync(HttpClient client, Guid product)
    {
        var response = await client.PostAsJsonAsync("/api/claims/mark-ready", new { productId = product, finalDescription = CleanText, issueDecisions = Array.Empty<object>() });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<MarkReadyResponse>();
        Assert.True(body!.ReadyToPublishSubjectToReview, string.Join(" ", body.ValidationErrors));
    }

    private static object[] KeepEverything(IEnumerable<GroupedFinding> findings) =>
        findings.Select(f => (object)new
        {
            issueId = f.IssueId,
            severity = f.Severity.ToUpperInvariant(),
            userDecision = ComplianceDecision.KeptOriginalWithJustification,
            userJustification = GoodReason,
            requiresEvidence = f.RequiresEvidence,
            resolvedSentenceSignature = f.SentenceSignature
        }).ToArray();

    // Four eyes

    [Fact]
    public async Task ASeniorEditorCannotSignOffTheirOwnSubmission_ButAnotherOneCan()
    {
        var author = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(author, "Four eyes shirt");
        await SubmitCleanAsync(author, product);

        var own = await author.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        var ownBody = await own.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);
        Assert.False(ownBody!.Success);
        Assert.Contains("another Senior Editor has to sign it off", ownBody.Message);

        var refused = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product && e.Action == AuditActions.Publish).ToListAsync());
        Assert.Contains(refused, e => e.Outcome == AuditOutcomes.Blocked && e.Detail!.StartsWith("Self sign-off refused"));

        var other = await _factory.CreateSeniorEditor(NewUser()).PostAsJsonAsync("/api/claims/publish", new { productId = product });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task TheQueueSaysWhichSubmissionsAreTheCallersOwn()
    {
        var author = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(author, "Own row shirt");
        await SubmitCleanAsync(author, product);

        Task<JsonElement> RowFor(HttpClient client) => client.GetFromJsonAsync<JsonElement>("/api/claims/pending-review")
            .ContinueWith(t => t.Result.EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product));

        var mine = await RowFor(author);
        Assert.True(mine.GetProperty("isOwnSubmission").GetBoolean());
        Assert.False(mine.GetProperty("canSignOff").GetBoolean());

        var theirs = await RowFor(_factory.CreateSeniorEditor(NewUser()));
        Assert.False(theirs.GetProperty("isOwnSubmission").GetBoolean());
        Assert.True(theirs.GetProperty("canSignOff").GetBoolean());
    }

    [Fact]
    public async Task WhenThereIsOnlyOneSeniorEditor_TheyMaySignOffTheirOwn_AndItIsFlagged()
    {
        using var single = _factory.WithWebHostBuilder(b => b.UseSetting("Policy:SingleSeniorEditor", "true"));
        var author = single.CreateClient();
        var userId = NewUser();
        author.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        author.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, string.Join(",", "analyse:claims", ApiTestFactory.SeniorEditorPermission, ApiTestFactory.OverridePermission, ApiTestFactory.EditFactsPermission));

        var product = await CreateProductAsync(author, "Solo editor shirt");
        await SubmitCleanAsync(author, product);
        var row = (await author.GetFromJsonAsync<JsonElement>("/api/claims/pending-review")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.True(row.GetProperty("canSignOff").GetBoolean());

        var response = await author.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        var body = await response.Content.ReadFromJsonAsync<PublishResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body!.SelfSignOff);
        var published = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product && e.Action == AuditActions.Publish && e.Outcome == AuditOutcomes.Ok).ToListAsync());
        Assert.Contains("Self sign-off", Assert.Single(published).Detail);
    }

    // Overriding

    [Fact]
    public async Task OverridingABlock_NeedsTheOverridePermission_NotJustTheSeniorEditorOne()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Override permission shirt");
        var findings = (await (await writer.PostAsJsonAsync("/api/analyze", new { text = CriticalText, rulesOnly = true })).Content.ReadFromJsonAsync<AnalyzeResponse>())!.GroupedFindings;
        var submitted = await writer.PostAsJsonAsync("/api/claims/mark-ready", new { productId = product, finalDescription = CriticalText, issueDecisions = KeepEverything(findings) });
        Assert.True((await submitted.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        const string reason = "Certificate checked against the supplier file today.";
        var basic = await _factory.CreateBasicSeniorEditor(NewUser()).PostAsJsonAsync("/api/claims/publish", new { productId = product, overrideReason = reason });
        var basicBody = await basic.Content.ReadFromJsonAsync<PublishResponse>();
        Assert.Equal(HttpStatusCode.Forbidden, basic.StatusCode);
        Assert.Contains(AuthorizationSetupPermission, basicBody!.Message);

        var full = await _factory.CreateSeniorEditor(NewUser()).PostAsJsonAsync("/api/claims/publish", new { productId = product, overrideReason = reason });
        Assert.Equal(HttpStatusCode.OK, full.StatusCode);
        Assert.True((await full.Content.ReadFromJsonAsync<PublishResponse>())!.Overridden);
    }

    private const string AuthorizationSetupPermission = "override:compliance";

    [Fact]
    public async Task ASeniorEditorWithoutTheOverridePermission_CanStillPublishCopyThatIsNotBlocked()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Plain cotton shirt");
        await SubmitCleanAsync(writer, product);

        var response = await _factory.CreateBasicSeniorEditor(NewUser()).PostAsJsonAsync("/api/claims/publish", new { productId = product });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Verified facts

    private static object FactsBody(string? reason, decimal cotton = 60, decimal polyester = 40) => new
    {
        materials = new object[] { new { material = "cotton", percentage = cotton }, new { material = "polyester", percentage = polyester } },
        certifications = new[] { "OEKO-TEX Standard 100" },
        origin = "Portugal",
        reason
    };

    [Fact]
    public async Task ChangingFacts_NeedsTheEditFactsPermission()
    {
        var product = await CreateProductAsync(_factory.CreateCopywriter(NewUser()), "Facts permission shirt");

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateBasicSeniorEditor(NewUser()).PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("Checked the sheet."))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter(NewUser()).PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("Checked the sheet."))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateSeniorEditor(NewUser()).PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("Checked the sheet."))).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("too short")]
    public async Task ChangingFacts_NeedsAReason(string? reason)
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(_factory.CreateCopywriter(NewUser()), "Facts reason shirt");

        var response = await editor.PutAsJsonAsync($"/api/products/{product}/facts", FactsBody(reason));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Say why the facts are changing", await response.Content.ReadAsStringAsync());
        Assert.False(await _factory.WithDbAsync(db => db.Products.Where(p => p.Id == product).SelectMany(p => p.Materials).AnyAsync()));
    }

    [Fact]
    public async Task AFactsChange_IsRecordedWithWhoWhyAndBeforeAndAfter()
    {
        var editorId = NewUser();
        var editor = _factory.CreateSeniorEditor(editorId);
        var product = await CreateProductAsync(_factory.CreateCopywriter(NewUser()), "Facts audit shirt");

        (await editor.PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("First entry from the supplier sheet."))).EnsureSuccessStatusCode();
        (await editor.PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("Supplier corrected the blend.", 70, 30))).EnsureSuccessStatusCode();

        var rows = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product && e.Action == AuditActions.FactsChange).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(editorId, r.UserId));
        Assert.Equal("Supplier corrected the blend.", rows[1].Justification);

        using var first = JsonDocument.Parse(rows[0].Detail!);
        Assert.Empty(first.RootElement.GetProperty("before").GetProperty("materials").EnumerateArray());
        using var second = JsonDocument.Parse(rows[1].Detail!);
        Assert.Equal(60, second.RootElement.GetProperty("before").GetProperty("materials")[0].GetProperty("Percentage").GetDecimal());
        Assert.Equal(70, second.RootElement.GetProperty("after").GetProperty("materials")[0].GetProperty("Percentage").GetDecimal());
    }

    [Fact]
    public async Task AFailedFactsChange_LeavesNoAuditRow()
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(_factory.CreateCopywriter(NewUser()), "Facts invalid shirt");

        var response = await editor.PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("Trying a bad blend today.", 50, 30));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await _factory.WithDbAsync(db => db.AuditLedger.AnyAsync(e => e.ProductId == product && e.Action == AuditActions.FactsChange)));
    }

    // Data imports

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void DataImportsOnlyRunInDevelopment(string environment, bool allowed)
    {
        Assert.Equal(allowed, DataImportRules.IsAllowed(new FakeEnvironment(environment)));
    }

    [Fact]
    public async Task ADataImport_IsRecordedInTheAuditTrail_WithWhoRanIt()
    {
        var editorId = NewUser();
        var response = await _factory.CreateSeniorEditor(editorId).PostAsync("/api/data-transfer/seed", null);
        // checks it actually imports the seed file, not just says it did
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var imported = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(imported.GetProperty("success").GetBoolean(), imported.GetProperty("message").GetString());

        var rows = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.UserId == editorId && e.Action == AuditActions.DataTransfer).ToListAsync());
        Assert.Contains("Seed import", Assert.Single(rows).Detail);
    }

    [Fact]
    public async Task ACopywriterCannotRunADataImport()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter(NewUser()).PostAsync("/api/data-transfer/seed", null)).StatusCode);
    }

    // The audit trail pages

    [Fact]
    public async Task TheAuditTrailPages_AndSaysHowManyRowsThereAre()
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(editor, "Paged audit shirt");
        for (var i = 0; i < 3; i++)
        {
            (await editor.PutAsJsonAsync($"/api/products/{product}/facts", FactsBody($"Reviewed the sheet, round {i + 1}."))).EnsureSuccessStatusCode();
        }

        var all = await editor.GetAsync($"/api/audit?productId={product}");
        Assert.Equal("3", all.Headers.GetValues("X-Total-Count").Single());

        var firstPage = await editor.GetFromJsonAsync<JsonElement>($"/api/audit?productId={product}&take=2&skip=0");
        var secondPage = await editor.GetFromJsonAsync<JsonElement>($"/api/audit?productId={product}&take=2&skip=2");
        Assert.Equal(2, firstPage.GetArrayLength());
        Assert.Equal(1, secondPage.GetArrayLength());
        Assert.NotEqual(firstPage[0].GetProperty("id").GetInt64(), secondPage[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task TheAuditPageCountIsReadableByTheBrowser()
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/audit?take=1");
        request.Headers.Add("Origin", "http://localhost:8081");

        var response = await editor.SendAsync(request);

        Assert.Contains("X-Total-Count", string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers")));
    }

    // The lists behind the screens

    [Fact]
    public async Task TheFactsRequestInbox_ShowsWhatWritersAskedFor_ToSeniorEditorsOnly()
    {
        var writerId = NewUser();
        var writer = _factory.CreateCopywriter(writerId);
        writer.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "ada@brand.com");
        var product = await CreateProductAsync(writer, "Inbox shirt");
        (await writer.GetAsync("/api/me")).EnsureSuccessStatusCode(); // records the email
        (await writer.PostAsJsonAsync($"/api/products/{product}/facts-requests", new { note = "Composition and origin please." })).EnsureSuccessStatusCode();

        var inbox = await _factory.CreateSeniorEditor(NewUser()).GetFromJsonAsync<JsonElement>("/api/facts-requests");
        var row = inbox.EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal("Inbox shirt", row.GetProperty("productName").GetString());
        Assert.Equal("Composition and origin please.", row.GetProperty("note").GetString());
        Assert.Equal("ada@brand.com", row.GetProperty("requestedByEmail").GetString());
        Assert.False(row.GetProperty("hasFacts").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync("/api/facts-requests")).StatusCode);
    }

    [Fact]
    public async Task MyActivity_ShowsOnlyTheCallersOwnActions_WithoutTheCopy()
    {
        var me = _factory.CreateCopywriter(NewUser());
        var someoneElse = _factory.CreateCopywriter(NewUser());
        var mine = await CreateProductAsync(me, "My activity shirt");
        var theirs = await CreateProductAsync(someoneElse, "Their activity shirt");
        await SubmitCleanAsync(me, mine);
        await SubmitCleanAsync(someoneElse, theirs);

        var activity = await me.GetFromJsonAsync<JsonElement>("/api/my/activity");
        var rows = activity.EnumerateArray().ToList();

        Assert.Contains(rows, r => r.GetProperty("action").GetString() == AuditActions.Submit && r.GetProperty("productName").GetString() == "My activity shirt");
        Assert.DoesNotContain(rows, r => r.GetProperty("productName").ValueKind == JsonValueKind.String && r.GetProperty("productName").GetString() == "Their activity shirt");
        Assert.All(rows, r => Assert.False(r.TryGetProperty("copySnapshot", out _)));
    }

    [Fact]
    public async Task MyActivity_NeverShowsTheBeforeAndAfterOfAFactsChange()
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(editor, "Activity facts shirt");
        (await editor.PutAsJsonAsync($"/api/products/{product}/facts", FactsBody("Verified against the sheet."))).EnsureSuccessStatusCode();

        var rows = (await editor.GetFromJsonAsync<JsonElement>("/api/my/activity")).EnumerateArray().Where(r => r.GetProperty("action").GetString() == AuditActions.FactsChange).ToList();

        var row = Assert.Single(rows);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("detail").ValueKind);
        Assert.Equal("Verified against the sheet.", row.GetProperty("justification").GetString());
    }
}
