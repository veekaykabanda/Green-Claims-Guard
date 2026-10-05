using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// a bullet line with no punctuation would get merged with the next line by a naive split, so now a line break counts as the end of a "sentence" too
public class SentenceBoundaryTests
{
    private sealed class NoCases : ICaseReferenceService
    {
        public List<CaseReference> GetCasesForRuleIds(IEnumerable<string> ruleIds) => new();
    }

    [Fact]
    public void AnUnpunctuatedBulletLine_DoesNotSwallowTheNextLine()
    {
        var aggregator = new AggregationService(new NoCases());
        const string copy =
            "Sustainable bamboo fibres\n" +
            "Machine washable at 30 degrees.";

        var finding = new RuleFinding
        {
            RuleId = "FASH-2",
            Category = "bamboo_fabric_misleading",
            Severity = "high",
            MatchedPattern = "sustainable bamboo",
            Regulation = "Test",
            Explanation = "Test",
        };

        var signature = aggregator.Aggregate(new() { finding }, new LlmResult(), copy)
            .GroupedFindings.Single().SentenceSignature;

        Assert.DoesNotContain("machine washable", signature);
        Assert.DoesNotContain("30 degrees", signature);
    }

    // real copy is often one line of "·"-separated bits with no punctuation, without this fix the whole line counts as one "sentence" and editing one bit would overwrite the others
    [Fact]
    public void ABulletSeparatedLine_KeepsEachFragmentSeparate()
    {
        var aggregator = new AggregationService(new NoCases());
        const string copy = "Round neck · Long sleeves · Made with organic cotton · Machine wash according to label";

        var finding = new RuleFinding
        {
            RuleId = "FASH-1",
            Category = "organic_cotton_certification",
            Severity = "high",
            MatchedPattern = "organic cotton",
            Regulation = "Test",
            Explanation = "Test",
        };

        var signature = aggregator.Aggregate(new() { finding }, new LlmResult(), copy)
            .GroupedFindings.Single().SentenceSignature;

        Assert.Contains("organic cotton", signature);
        Assert.DoesNotContain("round neck", signature);
        Assert.DoesNotContain("long sleeves", signature);
        Assert.DoesNotContain("machine wash", signature);
    }
}
