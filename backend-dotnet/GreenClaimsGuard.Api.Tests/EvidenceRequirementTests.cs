using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// which categories need the evidence form is decided here on purpose, not just left to whatever AggregationService does
public class EvidenceRequirementTests
{
    private sealed class NoCases : ICaseReferenceService
    {
        public List<CaseReference> GetCasesForRuleIds(IEnumerable<string> ruleIds) => new();
    }

    private static readonly AggregationService Aggregator = new(new NoCases());
    private const string AnyText = "Some product copy that mentions each phrase.";

    private static RuleFinding Finding(string category, string severity = "medium") => new()
    {
        RuleId = $"{category}-test",
        Category = category,
        Severity = severity,
        MatchedPattern = "phrase",
        Regulation = "Test",
        Explanation = "Test",
    };

    private static bool RequiresEvidence(string category) =>
        Aggregator.Aggregate(new() { Finding(category) }, new LlmResult(), AnyText)
            .GroupedFindings.Single().RequiresEvidence;

    [Theory]
    [InlineData("reduction_claims")]   // CMA-16
    [InlineData("plastic_reduction")]  // ASA-8
    [InlineData("fair_meaningful_comparisons")] // CMA-4
    [InlineData("comparative_claims")] // ASA-15
    public void GenericReductionOrComparisonCategories_RequireEvidence(string category)
    {
        Assert.True(RequiresEvidence(category));
    }

    // FASH-15 is the only rule out of 64 that mentions water, added on purpose, not from a generic keyword sweep
    [Fact]
    public void WaterlessDyeingClaims_RequiresEvidence()
    {
        Assert.True(RequiresEvidence("waterless_dyeing_claims"));
    }

    // no other fashion category needs this form, those get fixed with the AI rewrite or a certificate instead
    [Theory]
    [InlineData("organic_cotton_certification")]
    [InlineData("bamboo_fabric_misleading")]
    [InlineData("recycled_polyester_specificity")]
    [InlineData("fashion_carbon_neutral")]
    [InlineData("fashion_future_commitments")]
    public void OtherFashionCategories_DoNotRequireEvidence(string category)
    {
        Assert.False(RequiresEvidence(category));
    }
}
