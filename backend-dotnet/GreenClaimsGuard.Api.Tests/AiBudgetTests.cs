using System.Net;
using System.Net.Http.Json;
using GreenClaimsGuard.Api.Services;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

public class AiBudgetTests
{
    private static readonly DateTime Start = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AllowsTenAiChecksAMinute_ThenRefuses_ThenAllowsAgainInTheNextMinute()
    {
        var budget = new AiBudget();

        for (var i = 0; i < AiBudget.PerMinute; i++)
        {
            Assert.True(budget.TryTake("user-a", Start.AddSeconds(i)));
        }
        Assert.False(budget.TryTake("user-a", Start.AddSeconds(30)));
        Assert.True(budget.TryTake("user-a", Start.AddSeconds(61)));
    }

    [Fact]
    public void OnePersonsChecksDoNotUseUpAnothersBudget()
    {
        var budget = new AiBudget();
        for (var i = 0; i < AiBudget.PerMinute; i++) budget.TryTake("user-a", Start);

        Assert.False(budget.TryTake("user-a", Start));
        Assert.True(budget.TryTake("user-b", Start));
    }
}

[Collection(ApiCollection.Name)]
public class AnalyzeBudgetEndpointTests
{
    private readonly ApiTestFactory _factory;

    public AnalyzeBudgetEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RulesOnlyChecksAreNotLimitedLikeAiChecks_ButAiChecksAre()
    {
        var client = _factory.CreateCopywriter();
        var text = "A plain cotton shirt with a relaxed fit.";

        // way more rules-only checks than the ai budget allows, that's fine since they're free
        for (var i = 0; i < AiBudget.PerMinute + 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/analyze", new { text, rulesOnly = true });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < AiBudget.PerMinute + 2; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/analyze", new { text, rulesOnly = false })).StatusCode);
        }

        Assert.Equal(AiBudget.PerMinute, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}
