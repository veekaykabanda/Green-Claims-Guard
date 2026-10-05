using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// a copywriter's work is theirs alone, other people's products don't show up in lists or by id, and refusals don't say what they're refusing. editor can open everything
[Collection(ApiCollection.Name)]
public class OwnershipTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string MediumOnlyText = "Made with recycled polyester yarn.";

    private readonly ApiTestFactory _factory;

    public OwnershipTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static string NewUser(string prefix) => $"auth0|{prefix}-{Guid.NewGuid():N}";

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<MarkReadyResponse> SubmitAsync(HttpClient client, Guid productId, string text, object[]? decisions = null)
    {
        var response = await client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId,
            finalDescription = text,
            issueDecisions = decisions ?? Array.Empty<object>()
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MarkReadyResponse>())!;
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string url) =>
        await client.GetFromJsonAsync<JsonElement>(url);

    private static IEnumerable<Guid> Ids(JsonElement list, string property) =>
        list.EnumerateArray().Select(i => i.GetProperty(property).GetGuid());

    [Fact]
    public async Task ACopywriter_CannotOpenAnotherCopywritersProduct_ByAnyRoute()
    {
        var writerA = _factory.CreateCopywriter(NewUser("a"));
        var writerB = _factory.CreateCopywriter(NewUser("b"));
        var product = await CreateProductAsync(writerB, "Bs secret product");

        Assert.Equal(HttpStatusCode.Forbidden, (await writerA.GetAsync($"/api/products/{product}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writerA.GetAsync($"/api/products/{product}/facts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writerA.GetAsync($"/api/products/{product}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writerA.PatchAsJsonAsync($"/api/products/{product}", new { name = "Hijacked" })).StatusCode);

        // owner still can, and the rename didn't happen
        var opened = await writerB.GetFromJsonAsync<JsonElement>($"/api/products/{product}");
        Assert.Equal("Bs secret product", opened.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ARefusal_IsPlain_AndSaysNothingAboutTheProduct()
    {
        var writerA = _factory.CreateCopywriter(NewUser("a"));
        var product = await CreateProductAsync(_factory.CreateCopywriter(NewUser("b")), "Confidential Linen Dress");

        var response = await writerA.GetAsync($"/api/products/{product}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(Problems.ForbiddenDetail, body);
        Assert.DoesNotContain("Confidential", body);
        Assert.DoesNotContain(product.ToString(), body);
    }

    [Fact]
    public async Task AnUnknownProduct_IsNotFound()
    {
        var writer = _factory.CreateCopywriter(NewUser("a"));
        var nothing = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{nothing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{nothing}/facts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{nothing}/history")).StatusCode);
    }

    [Fact]
    public async Task OtherPeoplesWork_NeverAppearsInAListing()
    {
        var writerA = _factory.CreateCopywriter(NewUser("a"));
        var writerB = _factory.CreateCopywriter(NewUser("b"));
        var mine = await CreateProductAsync(writerA, "Listing mine");
        var theirs = await CreateProductAsync(writerB, "Listing theirs");
        await SubmitAsync(writerA, mine, CleanText);
        await SubmitAsync(writerB, theirs, CleanText);

        var products = Ids(await ListAsync(writerA, "/api/products"), "id").ToList();
        Assert.Contains(mine, products);
        Assert.DoesNotContain(theirs, products);

        var submissions = Ids(await ListAsync(writerA, "/api/my/submissions"), "productId").ToList();
        Assert.Contains(mine, submissions);
        Assert.DoesNotContain(theirs, submissions);
    }

    [Fact]
    public async Task Search_OnlyEverSearchesTheCallersOwnProducts()
    {
        var writerA = _factory.CreateCopywriter(NewUser("a"));
        var writerB = _factory.CreateCopywriter(NewUser("b"));
        var word = $"zebra{Guid.NewGuid():N}"[..14];
        var mine = await CreateProductAsync(writerA, $"{word} tee");
        await CreateProductAsync(writerB, $"{word} coat");

        var found = Ids(await ListAsync(writerA, $"/api/products?search={word}"), "id").ToList();

        Assert.Equal(new[] { mine }, found);
    }

    [Fact]
    public async Task ASeniorEditor_CanOpenAndListEveryonesProducts()
    {
        var writer = _factory.CreateCopywriter(NewUser("a"));
        var editor = _factory.CreateSeniorEditor();
        var word = $"otter{Guid.NewGuid():N}"[..13];
        var product = await CreateProductAsync(writer, $"{word} shirt");
        await SubmitAsync(writer, product, CleanText);

        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"/api/products/{product}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"/api/products/{product}/facts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.GetAsync($"/api/products/{product}/history")).StatusCode);
        Assert.Contains(product, Ids(await ListAsync(editor, $"/api/products?search={word}"), "id"));

        // "my submissions" is always about your own work, even for an editor
        Assert.DoesNotContain(product, Ids(await ListAsync(editor, "/api/my/submissions"), "productId"));
    }

    [Fact]
    public async Task ACheckAboutSomeoneElsesProduct_IsRefused_ButAnUnknownProductIsIgnored()
    {
        var writerA = _factory.CreateCopywriter(NewUser("a"));
        var theirs = await CreateProductAsync(_factory.CreateCopywriter(NewUser("b")), "Analyse theirs");

        var refused = await writerA.PostAsJsonAsync("/api/analyze", new { text = CleanText, rulesOnly = true, productId = theirs });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var ignored = await writerA.PostAsJsonAsync("/api/analyze", new { text = CleanText, rulesOnly = true, productId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, ignored.StatusCode);
    }

    [Fact]
    public async Task ASubmissionForSomeoneElsesProduct_IsRefused_AndNothingIsSaved()
    {
        var writerA = _factory.CreateCopywriter(NewUser("a"));
        var theirs = await CreateProductAsync(_factory.CreateCopywriter(NewUser("b")), "Submit theirs");

        var response = await writerA.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = theirs,
            finalDescription = CleanText,
            issueDecisions = Array.Empty<object>()
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var pending = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/claims/pending-review");
        Assert.DoesNotContain(theirs, Ids(pending, "productId"));
    }

    [Fact]
    public async Task AProductWithNoRecordedCreator_BelongsToSeniorEditorsOnly()
    {
        var legacy = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Products.Add(new Product { Id = legacy, Name = "Legacy product", CreatedByUserId = null });
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter(NewUser("a")).GetAsync($"/api/products/{legacy}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateSeniorEditor().GetAsync($"/api/products/{legacy}")).StatusCode);
    }

    [Fact]
    public async Task MySubmissions_ShowsStatus_Market_OpenIssues_AndAiStatus_AndFollowsThePublish()
    {
        var writer = _factory.CreateCopywriter(NewUser("a"));
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Status product");

        // before submitting, the product has no submission yet and is just a draft
        Assert.DoesNotContain(product, Ids(await ListAsync(writer, "/api/my/submissions"), "productId"));
        var draft = (await ListAsync(writer, "/api/products")).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == product);
        Assert.Equal("Draft", draft.GetProperty("status").GetString());

        var submit = await writer.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product, finalDescription = CleanText, market = "EU", issueDecisions = Array.Empty<object>()
        });
        Assert.True((await submit.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        var row = (await ListAsync(writer, "/api/my/submissions")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal("Status product", row.GetProperty("productName").GetString());
        Assert.Equal("InReview", row.GetProperty("status").GetString());
        Assert.Equal("EU", row.GetProperty("market").GetString());
        Assert.Equal(0, row.GetProperty("openIssueCount").GetInt32());
        Assert.Equal("Ok", row.GetProperty("aiStatus").GetString());

        await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product });

        var published = (await ListAsync(writer, "/api/my/submissions")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal("Published", published.GetProperty("status").GetString());
        // market and issue count stay what they were at submission, publishing doesn't change them
        Assert.Equal("EU", published.GetProperty("market").GetString());
    }

    [Fact]
    public async Task History_ListsEachVersion_TheWritersKeepReasons_AndThePublishDate()
    {
        var writer = _factory.CreateCopywriter(NewUser("a"));
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "History product");

        var analysis = await writer.PostAsJsonAsync("/api/analyze", new { text = MediumOnlyText, rulesOnly = true });
        var findings = (await analysis.Content.ReadFromJsonAsync<AnalyzeResponse>())!.GroupedFindings;
        Assert.NotEmpty(findings);

        var decisions = findings.Select(f => (object)new
        {
            issueId = f.IssueId,
            severity = f.Severity.ToUpperInvariant(),
            userDecision = ComplianceDecision.KeptOriginalWithJustification,
            userJustification = "Supplier certificate on file, ref GRS-2026-114.",
            requiresEvidence = f.RequiresEvidence,
            resolvedSentenceSignature = f.SentenceSignature
        }).ToArray();
        Assert.True((await SubmitAsync(writer, product, MediumOnlyText, decisions)).ReadyToPublishSubjectToReview);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product })).StatusCode);

        var history = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/history");

        Assert.Equal("Published", history.GetProperty("status").GetString());
        var version = Assert.Single(history.GetProperty("versions").EnumerateArray());
        Assert.Equal(1, version.GetProperty("version").GetInt32());
        Assert.Equal(MediumOnlyText, version.GetProperty("text").GetString());
        Assert.NotEqual(JsonValueKind.Null, version.GetProperty("publishedAt").ValueKind);
        var kept = version.GetProperty("decisions").EnumerateArray().First();
        Assert.Equal("Kept", kept.GetProperty("decision").GetString());
        Assert.Equal("Supplier certificate on file, ref GRS-2026-114.", kept.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ARefusedRequest_HasAPlainBody_ForNoTokenAndForNoPermission()
    {
        var anonymous = await _factory.CreateAnonymousClient().GetAsync("/api/products");
        var anonymousBody = await anonymous.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(Problems.SignInDetail, anonymousBody.GetProperty("detail").GetString());

        var forbidden = await _factory.CreateCopywriter(NewUser("a")).GetAsync("/api/audit");
        var forbiddenBody = await forbidden.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(Problems.ForbiddenDetail, forbiddenBody.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("/api/claims/recent")]
    [InlineData("/api/db-status")]
    public async Task EndpointsThatShowEveryonesData_AreForSeniorEditorsOnly(string path)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter(NewUser("a")).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateAnonymousClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateSeniorEditor().GetAsync(path)).StatusCode);
    }
}
