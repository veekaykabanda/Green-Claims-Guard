using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// expected scores are worked out by hand from the formula, not copied from the code, so these tests actually catch regressions
public class ComplianceScoreTests
{
    private sealed class NoCases : ICaseReferenceService
    {
        public List<CaseReference> GetCasesForRuleIds(IEnumerable<string> ruleIds) => new();
    }

    private static readonly AggregationService Aggregator = new(new NoCases());
    private const string AnyText = "Some product copy that mentions each phrase.";

    private static RuleFinding Finding(string category, string severity, string pattern = "phrase") => new()
    {
        RuleId = $"{category}-{pattern}",
        Category = category,
        Severity = severity,
        MatchedPattern = pattern,
        Regulation = "Test",
        Explanation = "Test",
    };

    private static AnalyzeResponse Score(List<RuleFinding> findings, LlmResult? ai = null) =>
        Aggregator.Aggregate(findings, ai ?? new LlmResult(), AnyText);

    // The score

    [Fact]
    public void NothingFound_IsAPerfectScore()
    {
        Assert.Equal(100, Score(new()).ComplianceScore);
    }

    [Theory]
    [InlineData("high", 88)]    // 100 - 12
    [InlineData("medium", 93)]  // 100 - 7
    [InlineData("low", 96)]     // 100 - 4
    public void OneFinding_CostsItsSeverityWeight(string severity, int expected)
    {
        Assert.Equal(expected, Score(new() { Finding("basis", severity) }).ComplianceScore);
    }

    [Theory]
    [InlineData(2, 86)]  // high 12 + one extra hit 2  = 14
    [InlineData(3, 84)]  // 12 + two extra hits 4      = 16
    [InlineData(4, 82)]  // 12 + three extra hits, capped at 6 = 18
    [InlineData(9, 82)]  // still capped, repeating a phrase can't tank the score
    public void RepeatHitsInOneCategory_AddALittle_UpToACap(int hits, int expected)
    {
        var findings = Enumerable.Range(0, hits).Select(i => Finding("basis", "high", $"phrase{i}")).ToList();

        Assert.Equal(expected, Score(findings).ComplianceScore);
    }

    [Fact]
    public void EachCategoryCountsOnce_SoTwoCategoriesAddUp()
    {
        var findings = new List<RuleFinding> { Finding("basis", "high"), Finding("lifecycle", "medium") };

        Assert.Equal(81, Score(findings).ComplianceScore); // 100 - 12 - 7
    }

    [Fact]
    public void ACategoryIsScoredByItsWorstFinding()
    {
        var findings = new List<RuleFinding> { Finding("basis", "low", "a"), Finding("basis", "high", "b") };

        // high (12) plus one extra hit (2), not low (4) plus one extra hit
        Assert.Equal(86, Score(findings).ComplianceScore);
    }

    [Fact]
    public void TheScoreNeverDropsBelowFive()
    {
        var findings = Enumerable.Range(0, 12).Select(i => Finding($"category-{i}", "high")).ToList();

        Assert.Equal(5, Score(findings).ComplianceScore);
    }

    // What the AI adds

    [Fact]
    public void IssuesOnlyTheAiFound_CostByTheAiRiskLevel()
    {
        var ai = new LlmResult { AiRiskLevel = "high", AiIssueCount = 3 };

        // rules found 1 (high, 12), ai says 3 in total so 2 more count at 8 each, 100 - 12 - 16
        Assert.Equal(72, Score(new() { Finding("basis", "high") }, ai).ComplianceScore);
    }

    [Fact]
    public void TheAiReportingNoMoreThanTheRulesFound_CostsNothingExtra()
    {
        var ai = new LlmResult { AiRiskLevel = "high", AiIssueCount = 1 };

        Assert.Equal(88, Score(new() { Finding("basis", "high") }, ai).ComplianceScore);
    }

    // The traffic light

    [Theory]
    [InlineData("high", "High", "\U0001F534")]
    [InlineData("medium", "Medium", "\U0001F7E1")]
    [InlineData("low", "Low", "\U0001F7E2")]
    public void TheTrafficLight_FollowsTheWorstRuleFinding(string severity, string risk, string light)
    {
        var result = Score(new() { Finding("basis", severity) });

        Assert.Equal(risk, result.OverallRisk);
        Assert.Equal(light, result.TrafficLight);
    }

    [Fact]
    public void NothingFound_IsGreen()
    {
        var result = Score(new());

        Assert.Equal("Low", result.OverallRisk);
        Assert.Equal("\U0001F7E2", result.TrafficLight);
    }

    [Theory]
    [InlineData("high", "High")]
    [InlineData("medium", "Medium")]
    public void AnAiThatSeesMoreRisk_RaisesTheLight_ButNeverLowersIt(string aiRisk, string expected)
    {
        var raised = Score(new(), new LlmResult { AiRiskLevel = aiRisk, AiIssueCount = 1 });
        Assert.Equal(expected, raised.OverallRisk);

        // rules already found a high, an ai saying "low" can't walk that back
        var notLowered = Score(new() { Finding("basis", "high") }, new LlmResult { AiRiskLevel = "low", AiIssueCount = 1 });
        Assert.Equal("High", notLowered.OverallRisk);
    }
}
