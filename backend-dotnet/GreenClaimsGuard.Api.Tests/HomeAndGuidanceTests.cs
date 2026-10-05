using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// a writer's Home and Guidance pages only show their own numbers, whether the AI is up, and guidance based on the rules actually enforced
[Collection(ApiCollection.Name)]
public class HomeAndGuidanceTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string CriticalText = "Our new range is made from sustainable bamboo fabric.";

    private readonly ApiTestFactory _factory;

    public HomeAndGuidanceTests(ApiTestFactory factory)
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

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid product, string text = CleanText) =>
        client.PostAsJsonAsync("/api/claims/mark-ready", new { productId = product, finalDescription = text, issueDecisions = Array.Empty<object>() });

    private static async Task CheckAsync(HttpClient client, string text, Guid? product = null) =>
        (await client.PostAsJsonAsync("/api/analyze", new { text, rulesOnly = true, trigger = "manual", productId = product })).EnsureSuccessStatusCode();

    // My overview

    [Fact]
    public async Task MyOverview_CountsOnlyMyOwnDraftsAwaitingSentBackAndPublished()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var stranger = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();

        var drafted = await CreateProductAsync(writer, "Overview drafted");
        await writer.PutAsJsonAsync($"/api/products/{drafted}/draft", new { text = "Words in progress here.", market = "UK" });
        var awaiting = await CreateProductAsync(writer, "Overview awaiting");
        await SubmitAsync(writer, awaiting);
        var sentBack = await CreateProductAsync(writer, "Overview sent back");
        await SubmitAsync(writer, sentBack);
        await editor.PostAsJsonAsync("/api/claims/send-back", new { productId = sentBack, reasonCategory = "not_accurate", comment = "This needs a certificate." });
        var published = await CreateProductAsync(writer, "Overview published");
        await SubmitAsync(writer, published);
        await editor.PostAsJsonAsync("/api/claims/publish", new { productId = published });

        // someone else's work shouldn't show up in any of these counts
        var strangers = await CreateProductAsync(stranger, "Not counted");
        await SubmitAsync(stranger, strangers);

        var overview = await writer.GetFromJsonAsync<MyOverviewResponse>("/api/my/overview");

        // send-back restores a draft too, so there's 2 now, the typed one and the handed-back one
        Assert.Equal(2, overview!.Drafts);
        Assert.Equal(1, overview.AwaitingSignOff);
        Assert.Equal(1, overview.SentBack);
        Assert.Equal(1, overview.Published);

        var strangerOverview = await stranger.GetFromJsonAsync<MyOverviewResponse>("/api/my/overview");
        Assert.Equal(1, strangerOverview!.AwaitingSignOff);
        Assert.Equal(0, strangerOverview.Published);
    }

    [Fact]
    public async Task MyOverview_HasNoCompanyWideNumbers()
    {
        var overview = await _factory.CreateCopywriter(NewUser()).GetFromJsonAsync<JsonElement>("/api/my/overview");

        var names = overview.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "generatedAt", "drafts", "awaitingSignOff", "sentBack", "published", "topPhrases" }, names);
    }

    [Fact]
    public async Task TheTopPhrases_AreFromMyOwnChecksOnly_AndCarryTheirCategory()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var other = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Phrase product");
        for (var i = 0; i < 3; i++) await CheckAsync(writer, CriticalText, product);
        await CheckAsync(writer, "A soft eco-friendly shirt.");
        await CheckAsync(other, "Totally carbon neutral hoodie.");

        var overview = await writer.GetFromJsonAsync<MyOverviewResponse>("/api/my/overview");

        var top = overview!.TopPhrases;
        Assert.InRange(top.Count, 1, 5);
        Assert.Equal(3, top[0].Count);
        Assert.Contains("bamboo", top[0].Phrase);
        Assert.False(string.IsNullOrWhiteSpace(top[0].Category));
        Assert.DoesNotContain(top, p => p.Phrase.Contains("carbon neutral"));
        Assert.Contains(top, p => p.Phrase.Contains("eco-friendly"));
    }

    [Fact]
    public async Task TheTopPhrases_AreCappedAtFive()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        foreach (var text in new[]
                 {
                     "Made with recycled polyester yarn.", "Our eco-friendly tee.", "A sustainable range.",
                     "Totally carbon neutral.", "This is biodegradable fabric.", "Made from organic cotton.", "A green jacket."
                 })
        {
            await CheckAsync(writer, text);
        }

        var overview = await writer.GetFromJsonAsync<MyOverviewResponse>("/api/my/overview");

        Assert.Equal(5, overview!.TopPhrases.Count);
    }

    [Fact]
    public async Task TheCompanyWideOverview_IsForSeniorEditorsOnly()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateCopywriter(NewUser()).GetAsync("/api/dashboard/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateSeniorEditor().GetAsync("/api/dashboard/overview")).StatusCode);
    }

    // Status

    [Fact]
    public async Task ACopywritersStatus_IsOnlyWhetherTheAiIsAvailable()
    {
        var status = await _factory.CreateCopywriter(NewUser()).GetFromJsonAsync<JsonElement>("/api/status");

        Assert.Equal(new[] { "checkedAt", "ai" }, status.EnumerateObject().Select(p => p.Name).ToArray());
        var ai = status.GetProperty("ai");
        Assert.Equal(new[] { "available", "label" }, ai.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Contains(ai.GetProperty("label").GetString(), new[] { "AI check available", "AI check unavailable" });
        Assert.DoesNotContain("UK-", status.GetRawText());
        Assert.DoesNotContain("Unavailable", status.GetRawText());
    }

    [Fact]
    public async Task ASeniorEditorsStatus_StillHasEverything()
    {
        var status = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/status");

        Assert.True(status.TryGetProperty("rules", out _));
        Assert.True(status.TryGetProperty("database", out _));
        Assert.True(status.GetProperty("ai").TryGetProperty("available", out _));
    }

    // Guidance

    [Theory]
    [InlineData("UK")]
    [InlineData("EU")]
    public async Task Guidance_ListsExactlyTheRulesTheCheckerEnforces(string market)
    {
        var rules = _factory.Services.GetRequiredService<IRuleEngineService>();

        var guidance = await _factory.CreateCopywriter(NewUser()).GetFromJsonAsync<JsonElement>($"/api/guidance?market={market}");

        Assert.Equal(market, guidance.GetProperty("market").GetString());
        Assert.Equal(rules.RulesVersionFor(market), guidance.GetProperty("rulesVersion").GetString());
        Assert.Equal(rules.RuleCountFor(market), guidance.GetProperty("rules").GetArrayLength());
        Assert.All(guidance.GetProperty("rules").EnumerateArray(), r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("regulation").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("explanation").GetString()));
            Assert.NotEmpty(r.GetProperty("examples").EnumerateArray());
        });
    }

    [Fact]
    public async Task Guidance_SaysTheSameThingAsTheCheckerDoes()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var analysis = await (await writer.PostAsJsonAsync("/api/analyze", new { text = "A soft, eco-friendly shirt.", rulesOnly = true, market = "EU" }))
            .Content.ReadFromJsonAsync<AnalyzeResponse>();
        var finding = Assert.Single(analysis!.GroupedFindings);

        var guidance = await writer.GetFromJsonAsync<JsonElement>("/api/guidance?market=EU");

        var rule = guidance.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == finding.RuleIds[0]);
        Assert.Equal(finding.Regulation, rule.GetProperty("regulation").GetString());
        Assert.Equal(finding.Explanation, rule.GetProperty("explanation").GetString());
    }

    [Fact]
    public async Task TheUkGuidance_CitesTheCmaCodeAndCapRules11_3And11_4_AndNothingFromTheEu()
    {
        var text = (await _factory.CreateCopywriter(NewUser()).GetFromJsonAsync<JsonElement>("/api/guidance?market=UK")).GetRawText();

        Assert.Contains("CMA Green Claims Code", text);
        Assert.Contains("11.3", text);
        Assert.Contains("11.4", text);
        Assert.DoesNotContain("2024/825", text);
        Assert.DoesNotContain("ECGT-", text);
    }

    [Fact]
    public async Task TheEuGuidance_ExplainsTheEcgtBan_AndNeverCallsTheGreenClaimsDirectiveLaw()
    {
        var guidance = await _factory.CreateCopywriter(NewUser()).GetFromJsonAsync<JsonElement>("/api/guidance?market=EU");
        var text = guidance.GetRawText();

        Assert.Contains("2024/825", text);
        Assert.Contains("27 September 2026", text);
        Assert.Contains("eco-friendly", text);
        Assert.Contains("recognised excellent environmental performance", text);
        Assert.DoesNotContain("CMA", text);
        Assert.DoesNotContain("CAP Code", text);

        // Green Claims Directive can get mentioned, but only to say it's not in force yet
        Assert.Contains("not in force", guidance.GetProperty("framework").GetProperty("note").GetString());
        Assert.DoesNotMatch(new Regex(@"Green Claims Directive (is|as|became) (law|in force|applies)", RegexOptions.IgnoreCase), text);
    }

    [Fact]
    public async Task Guidance_DefaultsToTheUk_AndRefusesOtherMarkets()
    {
        var writer = _factory.CreateCopywriter(NewUser());

        Assert.Equal("UK", (await writer.GetFromJsonAsync<JsonElement>("/api/guidance")).GetProperty("market").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.GetAsync("/api/guidance?market=US")).StatusCode);
    }

    [Fact]
    public async Task TheNewEndpoints_NeedASignedInUser()
    {
        var anonymous = _factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/my/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/guidance")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/status")).StatusCode);
    }
}
