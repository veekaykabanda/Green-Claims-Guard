using GreenClaimsGuard.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// same phrase in real copy should always raise the same rule at the same severity
[Collection(ApiCollection.Name)]
public class RuleSeverityTests
{
    private readonly IRuleEngineService _rules;

    public RuleSeverityTests(ApiTestFactory factory)
    {
        _rules = factory.Services.GetRequiredService<IRuleEngineService>();
    }

    [Theory]
    [InlineData("Our whole range is carbon neutral.", "CMA-6", "high")]
    [InlineData("Made in a factory running on renewable energy.", "ASA-9", "medium")]
    [InlineData("A greener choice for your wardrobe.", "ASA-15", "low")]
    public void ARealPhrase_RaisesTheRuleAtItsSeverity(string text, string ruleId, string severity)
    {
        var finding = Assert.Single(_rules.Analyze(text), f => f.RuleId == ruleId);

        Assert.Equal(severity, finding.Severity);
    }

    // "carbon neutral" shows up in a few rule files, only the first one (CMA) reports it so the writer sees it once
    [Fact]
    public void APhraseSeveralRuleFilesCover_IsReportedOnce_ByTheFirstFile()
    {
        var findings = _rules.Analyze("Our whole range is carbon neutral.");

        var found = Assert.Single(findings, f => f.MatchedPattern.Equals("carbon neutral", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("CMA-6", found.RuleId);
        Assert.DoesNotContain(findings, f => f.RuleId == "ASA-3");
    }

    [Fact]
    public void TheSamePhrase_IsFoundWhateverTheCase()
    {
        var lower = _rules.Analyze("Our range is carbon neutral.").Select(f => f.RuleId).OrderBy(x => x);
        var shouting = _rules.Analyze("OUR RANGE IS CARBON NEUTRAL.").Select(f => f.RuleId).OrderBy(x => x);

        Assert.NotEmpty(lower);
        Assert.Equal(lower, shouting);
    }

    [Fact]
    public void PlainCopyWithNoEnvironmentalClaim_RaisesNothing()
    {
        Assert.Empty(_rules.Analyze("A plain cotton shirt with a relaxed fit and two front pockets."));
    }

    [Fact]
    public void AFindingNamesTheExactWordsThatTriggeredIt()
    {
        var finding = Assert.Single(_rules.Analyze("Our whole range is carbon neutral."), f => f.RuleId == "CMA-6");

        Assert.Equal("carbon neutral", finding.MatchedPattern, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(finding.Regulation));
    }
}
