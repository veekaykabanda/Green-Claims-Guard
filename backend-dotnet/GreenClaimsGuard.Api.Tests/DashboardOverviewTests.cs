using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// db is shared across the whole test run, so we check counts went up, not exact numbers
[Collection(ApiCollection.Name)]
public class DashboardOverviewTests
{
    private readonly ApiTestFactory _factory;

    public DashboardOverviewTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<DashboardOverviewResponse?> OverviewAsync(HttpClient client) =>
        client.GetFromJsonAsync<DashboardOverviewResponse>("/api/dashboard/overview");

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/dashboard/overview");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ACriticalCheck_ShowsAsOpen_UntilTheCopyIsFixedAndSubmitted()
    {
        var copywriter = _factory.CreateCopywriter();
        var overviewEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "Overview product");
        var before = (await OverviewAsync(overviewEditor))!;

        var check = await copywriter.PostAsJsonAsync("/api/analyze", new
        {
            text = "Our overview range is made from sustainable bamboo fabric.",
            trigger = "manual",
            rulesOnly = true,
            productId = product
        });
        check.EnsureSuccessStatusCode();

        var during = (await OverviewAsync(overviewEditor))!;
        Assert.Equal(before.ProductsCheckedThisWeek + 1, during.ProductsCheckedThisWeek);
        Assert.True(during.OpenCriticalIssues > before.OpenCriticalIssues);
        Assert.Contains(during.MostFlaggedPhrases, p => p.Phrase.Contains("bamboo"));

        var submit = await copywriter.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = "A plain cotton shirt with a relaxed fit.",
            issueDecisions = Array.Empty<object>()
        });
        Assert.True((await submit.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        var after = (await OverviewAsync(overviewEditor))!;
        Assert.Equal(before.OpenCriticalIssues, after.OpenCriticalIssues);
        Assert.Equal(before.SubmissionsWaitingForPublish + 1, after.SubmissionsWaitingForPublish);
    }

    [Fact]
    public async Task ASubmissionWhoseAiCheckDidNotRun_CountsAsPendingAiReview()
    {
        var copywriter = _factory.CreateCopywriter();
        var overviewEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "AI pending product");
        var before = (await OverviewAsync(overviewEditor))!;

        using (_factory.Llm.Using(EngineStatus.Timeout))
        {
            var submit = await copywriter.PostAsJsonAsync("/api/claims/mark-ready", new
            {
                productId = product,
                finalDescription = "A plain cotton shirt with a relaxed fit.",
                issueDecisions = Array.Empty<object>()
            });
            Assert.True((await submit.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);
        }

        var after = (await OverviewAsync(overviewEditor))!;
        Assert.Equal(before.ItemsPendingAiReview + 1, after.ItemsPendingAiReview);
    }

    [Fact]
    public async Task AnOverride_IsCountedForTheMonth()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(copywriter, "Override counted");
        await copywriter.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = "A plain cotton shirt with a relaxed fit.",
            issueDecisions = Array.Empty<object>()
        });
        var before = (await OverviewAsync(seniorEditor))!;

        using (_factory.Llm.Using(EngineStatus.Failed))
        {
            var publish = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new
            {
                productId = product,
                overrideReason = "AI provider outage; copy read and approved by hand."
            });
            Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        }

        var after = (await OverviewAsync(seniorEditor))!;
        Assert.Equal(before.OverridesThisMonth + 1, after.OverridesThisMonth);
    }
}
