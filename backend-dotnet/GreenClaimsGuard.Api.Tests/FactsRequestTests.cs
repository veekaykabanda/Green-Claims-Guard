using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Endpoints;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// a facts request is just a saved note, it never changes the verified facts and the checker and AI never read it
[Collection(ApiCollection.Name)]
public class FactsRequestTests
{
    private const string Note = "Composition missing: is it 100% organic cotton?";

    private readonly ApiTestFactory _factory;

    public FactsRequestTests(ApiTestFactory factory)
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

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, Guid product, string? note = Note) =>
        client.PostAsJsonAsync($"/api/products/{product}/facts-requests", new { note });

    [Fact]
    public async Task AWriter_CanAskForFacts_AndSeeTheirRequest()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Asking product");

        var asked = await AskAsync(writer, product);

        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var created = await asked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Note, created.GetProperty("note").GetString());

        var list = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/facts-requests");
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal(Note, row.GetProperty("note").GetString());
        // writer doesn't see who asked, on their own product it's always them
        Assert.Equal(JsonValueKind.Null, row.GetProperty("requestedByUserId").ValueKind);
    }

    [Fact]
    public async Task ASeniorEditor_SeesWhoAsked()
    {
        var writer = _factory.CreateCopywriter("auth0|asking-writer-3");
        var product = await CreateProductAsync(writer, "Who asked product");
        await AskAsync(writer, product);

        var list = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>($"/api/products/{product}/facts-requests");

        Assert.Equal("auth0|asking-writer-3", Assert.Single(list.EnumerateArray()).GetProperty("requestedByUserId").GetString());
    }

    [Fact]
    public async Task OnlyTheProductsCreator_CanAsk_AndOnlyItsOwnerOrASeniorEditorCanRead()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Private request product");
        await AskAsync(writer, product);
        var other = _factory.CreateCopywriter(NewUser());

        Assert.Equal(HttpStatusCode.Forbidden, (await AskAsync(other, product)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AskAsync(_factory.CreateSeniorEditor(), product)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/products/{product}/facts-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(_factory.CreateAnonymousClient(), product)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(writer, Guid.NewGuid())).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("abcd")]
    public async Task ANoteMustSaySomething(string? note)
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Empty note product");

        Assert.Equal(HttpStatusCode.BadRequest, (await AskAsync(writer, product, note)).StatusCode);
    }

    [Fact]
    public async Task ANoteHasALimit()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Long note product");

        Assert.Equal(HttpStatusCode.BadRequest, (await AskAsync(writer, product, new string('n', FactsRequestEndpoints.MaxNoteLength + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await AskAsync(writer, product, new string('n', FactsRequestEndpoints.MaxNoteLength))).StatusCode);
    }

    [Fact]
    public async Task AskingCannotFloodASeniorEditor()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Flood product");

        for (var i = 0; i < FactsRequestEndpoints.MaxPerProductPerDay; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await AskAsync(writer, product, $"Request number {i} for facts.")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await AskAsync(writer, product, "One request too many.")).StatusCode);
    }

    [Fact]
    public async Task ARequest_NeverChangesTheVerifiedFacts()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Unchanged facts product");
        var before = await writer.GetFromJsonAsync<FactsResponse>($"/api/products/{product}/facts");

        await AskAsync(writer, product, "Please record that this is 100% GOTS organic cotton made in Portugal.");

        var after = await writer.GetFromJsonAsync<FactsResponse>($"/api/products/{product}/facts");
        Assert.Equal(FactsStatus.NoneOnFile, before!.Status);
        Assert.Equal(FactsStatus.NoneOnFile, after!.Status);
        Assert.Empty(after.Materials);
        Assert.Empty(after.Certifications);
        Assert.Null(after.Origin);
        Assert.Null(after.VerifiedByUserId);
    }

    [Fact]
    public async Task TheChecker_NeverTreatsANoteAsAFact()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Checker ignores note product");
        await AskAsync(writer, product, "The composition is 60% cotton and 40% polyester, honest.");

        // if the note counted as facts, "100% cotton" would contradict it, but it shouldn't
        var response = await writer.PostAsJsonAsync("/api/analyze", new { text = "A shirt in 100% cotton.", rulesOnly = true, productId = product });
        var analysis = (await response.Content.ReadFromJsonAsync<AnalyzeResponse>())!;

        Assert.Equal(FactsStatus.NoneOnFile, analysis.FactsStatus);
        Assert.DoesNotContain(analysis.GroupedFindings, f => f.Category == "product_facts_mismatch");

        var loaded = await _factory.Services.GetRequiredService<IProductFactsProvider>().LoadAsync(product);
        Assert.Equal(FactsStatus.NoneOnFile, loaded.Status);
        Assert.Null(loaded.Facts);
    }

    [Fact]
    public async Task TheAi_IsNeverShownANote()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "AI ignores note product");
        await AskAsync(writer, product, "Secretnoteword: it is made from unicorn leather.");

        var config = new ConfigurationBuilder().Build();
        var aiClient = new FakeAiComplianceClient();
        var orchestrator = new ComplianceOrchestrator(
            _factory.Services.GetRequiredService<IRuleEngineService>(),
            new LlmService(config, aiClient, NullLogger<LlmService>.Instance),
            _factory.Services.GetRequiredService<IAggregationService>(),
            new FakeDbService(),
            _factory.Services.GetRequiredService<IProductFactsProvider>(),
            NullLogger<ComplianceOrchestrator>.Instance);

        await orchestrator.EvaluateAsync(new ComplianceRequest
        {
            Text = "A plain cotton shirt with a relaxed fit.",
            Action = ComplianceAction.Analyze,
            RulesOnly = false,
            ProductId = product,
        });

        Assert.NotNull(aiClient.LastRequest);
        Assert.Null(aiClient.LastRequest!.VerifiedFacts);
        var prompt = CompliancePrompts.User(aiClient.LastRequest);
        Assert.DoesNotContain("Secretnoteword", prompt);
        Assert.DoesNotContain("unicorn", prompt);
        Assert.Contains("none are on file", prompt);
    }

    [Fact]
    public async Task DeletingAProduct_IsNotPossible_ButRequestsBelongToTheirProduct()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Belongs to product");
        await AskAsync(writer, product);

        var stored = await _factory.WithDbAsync(db => db.FactsRequests.CountAsync(r => r.ProductId == product));
        Assert.Equal(1, stored);
    }
}
