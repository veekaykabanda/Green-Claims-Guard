using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// same copy gets judged against the rules of whatever market it's sold in
[Collection(ApiCollection.Name)]
public class MarketTests
{
    private readonly ApiTestFactory _factory;

    public MarketTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static async Task<AnalyzeResponse> AnalyzeAsync(HttpClient client, string text, string? market)
    {
        var response = await client.PostAsJsonAsync("/api/analyze", new { text, rulesOnly = true, market });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AnalyzeResponse>())!;
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData("A soft, eco-friendly shirt.")]
    [InlineData("A soft, sustainable shirt.")]
    [InlineData("Our climate neutral hoodie.")]
    public async Task InTheEu_AGenericOrNeutralityClaim_IsCritical_AndNamesTheDirective(string text)
    {
        var result = await AnalyzeAsync(_factory.CreateCopywriter(), text, "EU");

        Assert.Equal("EU", result.Market);
        Assert.StartsWith("EU-", result.RulesVersion);
        var issue = Assert.Single(result.GroupedFindings, f => f.Severity == "high");
        Assert.Contains("2024/825", issue.Regulation);
        Assert.False(result.SubmitAllowed);
    }

    [Fact]
    public async Task InTheEu_NothingCitesTheUkRegulator()
    {
        var result = await AnalyzeAsync(_factory.CreateCopywriter(), "A soft, eco-friendly shirt. Carbon neutral too.", "EU");

        Assert.All(result.GroupedFindings, f =>
        {
            Assert.DoesNotContain("CMA", f.Regulation);
            Assert.DoesNotContain("ASA", f.Regulation);
            Assert.DoesNotContain("CAP", f.Regulation);
            Assert.Empty(f.CaseReferences);
        });
        Assert.All(result.References, r => Assert.Contains("eur-lex.europa.eu", r.Url));
        Assert.DoesNotContain(result.References, r => r.Name.Contains("Green Claims Directive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InTheUk_TheSameCopyIsJudgedUnderTheCmaAndCapRules_AndDefaultsToUk()
    {
        var explicitUk = await AnalyzeAsync(_factory.CreateCopywriter(), "Our climate neutral hoodie.", "UK");
        var defaulted = await AnalyzeAsync(_factory.CreateCopywriter(), "Our climate neutral hoodie.", null);

        Assert.Equal("UK", explicitUk.Market);
        Assert.Equal("UK", defaulted.Market);
        Assert.Equal(explicitUk.RulesVersion, defaulted.RulesVersion);
        Assert.Contains(explicitUk.GroupedFindings, f => f.Regulation.Contains("CMA") || f.Regulation.Contains("ASA"));
        Assert.DoesNotContain(explicitUk.GroupedFindings, f => f.Regulation.Contains("2024/825"));
        Assert.DoesNotContain(explicitUk.References, r => r.Url.Contains("eur-lex"));
    }

    [Fact]
    public async Task TheGreenClaimsDirectiveIsNeverCitedAsLaw_InEitherMarket()
    {
        foreach (var market in new[] { "UK", "EU" })
        {
            var result = await AnalyzeAsync(_factory.CreateCopywriter(), "Eco-friendly, sustainable, carbon neutral, green and 100% recycled.", market);
            Assert.DoesNotContain(result.GroupedFindings, f => f.Regulation.Contains("Green Claims Directive", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task AnUnsupportedMarket_IsRefused()
    {
        var response = await _factory.CreateCopywriter().PostAsJsonAsync("/api/analyze", new { text = "A plain shirt.", market = "US" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheMarketTheCopyWasSubmittedUnder_IsSavedOnTheLedger_AndUsedAgainAtPublish()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "EU submission");
        const string cleanCopy = "A plain cotton shirt with a relaxed fit.";

        var submit = await copywriter.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = cleanCopy,
            market = "EU",
            issueDecisions = Array.Empty<object>()
        });
        var body = (await submit.Content.ReadFromJsonAsync<MarkReadyResponse>())!;
        Assert.True(body.ReadyToPublishSubjectToReview);
        Assert.Equal("EU", body.Market);
        Assert.StartsWith("EU-", body.RulesVersion);

        var pending = await seniorEditor.GetFromJsonAsync<List<PendingReviewItem>>("/api/claims/pending-review");
        Assert.Equal("EU", pending!.Single(p => p.ProductId == product).Market);

        var publish = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var entries = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product).ToListAsync());
        Assert.NotEmpty(entries);
        Assert.All(entries.Where(e => e.Action is AuditActions.Submit or AuditActions.Publish), e =>
        {
            Assert.Equal("EU", e.Market);
            Assert.StartsWith("EU-", e.RulesVersion);
        });
    }

    [Fact]
    public async Task ACopySubmittedUnderTheUk_ButWithAnEuOnlyProblem_StillPassesUkRules_AndFailsEuRules()
    {
        // labels like "Conscious Collection" count as an unverified sustainability label under EU rules, but only a lower severity issue under UK rules
        var uk = await AnalyzeAsync(_factory.CreateCopywriter(), "From our eco edit.", "UK");
        var eu = await AnalyzeAsync(_factory.CreateCopywriter(), "From our eco edit.", "EU");

        Assert.Contains(eu.GroupedFindings, f => f.Severity == "high" && f.Regulation.Contains("point 2a"));
        Assert.DoesNotContain(uk.GroupedFindings, f => f.Regulation.Contains("point 2a"));
    }
}
