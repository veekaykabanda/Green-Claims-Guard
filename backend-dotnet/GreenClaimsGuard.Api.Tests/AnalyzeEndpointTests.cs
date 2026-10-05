using System.Net.Http.Json;
using GreenClaimsGuard.Api.Models;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

[Collection(ApiCollection.Name)]
public class AnalyzeEndpointTests
{
    private readonly HttpClient _client;

    public AnalyzeEndpointTests(ApiTestFactory factory)
    {
        _client = factory.CreateCopywriter();
    }

    [Fact]
    public async Task Analyze_DetectsHighSeverity_BambooFabricClaim()
    {
        var response = await _client.PostAsJsonAsync("/api/analyze", new
        {
            text = "Our new range is made from sustainable bamboo fabric.",
            rulesOnly = true
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AnalyzeResponse>();

        Assert.Contains(result!.RuleFindings, f => f.RuleId == "FASH-2" && f.Severity == "high");
    }

    [Fact]
    public async Task Analyze_DetectsMediumSeverity_RecycledPolyesterClaim()
    {
        var response = await _client.PostAsJsonAsync("/api/analyze", new
        {
            text = "Made with 100% recycled polyester yarn.",
            rulesOnly = true
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AnalyzeResponse>();

        Assert.Contains(result!.RuleFindings, f => f.RuleId == "FASH-3" && f.Severity == "medium");
    }

    [Fact]
    public async Task Analyze_DetectsLowSeverity_TencelClaim()
    {
        var response = await _client.PostAsJsonAsync("/api/analyze", new
        {
            text = "This top is made from Tencel lyocell.",
            rulesOnly = true
        });

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AnalyzeResponse>();

        Assert.Contains(result!.RuleFindings, f => f.RuleId == "FASH-6" && f.Severity == "low");
    }
}
