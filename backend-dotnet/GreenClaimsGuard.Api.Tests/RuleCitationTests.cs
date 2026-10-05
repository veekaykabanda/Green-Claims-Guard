using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// checks every CAP rule number we cite (3.x misleading ads, section 11 environmental) is real, matched against asa.org.uk
public class RuleCitationTests
{
    // the real CAP rule numbers these rule files are allowed to cite
    private static readonly HashSet<string> RealCapRules = new()
    {
        "3.1", "3.2", "3.3", "3.7", "3.9",
        "11.1", "11.2", "11.3", "11.4", "11.5", "11.6", "11.7",
    };

    // catches old CAP Code numbers like "Rule 11.1/9.2", the bit after the slash is from an old edition
    private static readonly Regex OldEditionAlternative = new(@"[Rr]ule \d+\.\d+/\d");

    private static JsonElement RulesIn(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Rules", file);
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("rules");
    }

    private static Dictionary<string, string> RegulationById(string file) =>
        RulesIn(file).EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("regulation").GetString()!);

    // grabs the number right after "Rule", works for "ASA CAP Rule 11.1", "CAP Code rule 3.1", "(Rule 11.3/9.4)" etc
    private static IEnumerable<string> CitedCapRules(string regulation) =>
        Regex.Matches(regulation, @"[Rr]ule (\d+\.\d+)").Select(m => m.Groups[1].Value);

    [Theory]
    [InlineData("fashion_rules.json")]
    [InlineData("asa_rules.json")]
    public void EveryCapRuleCited_IsARealCapRule(string file)
    {
        foreach (var (id, regulation) in RegulationById(file))
        {
            foreach (var cited in CitedCapRules(regulation))
            {
                Assert.True(RealCapRules.Contains(cited), $"{id} cites CAP rule {cited}, which is not a rule this app knows: \"{regulation}\"");
            }
        }
    }

    // a citation should be one rule number, not an old-edition one tacked on after a slash
    [Theory]
    [InlineData("fashion_rules.json")]
    [InlineData("asa_rules.json")]
    public void ACitationNamesOneCurrentRule_NotAnOldEditionAlternative(string file)
    {
        foreach (var (id, regulation) in RegulationById(file))
        {
            Assert.False(OldEditionAlternative.IsMatch(regulation), $"{id} still carries an old-edition rule number: \"{regulation}\"");
        }
    }

    // just checking the regex above actually catches what it's meant to
    [Theory]
    [InlineData("ASA CAP - Environmental claims must have clear basis (Rule 11.1/9.2)", true)]
    [InlineData("ASA CAP - Must not omit material information (Rule 3.3/3.2)", true)]
    [InlineData("ASA CAP - Environmental claims must have clear basis (Rule 11.1)", false)]
    [InlineData("CMA Green Claims Code; ASA CAP Rule 3.7 (substantiation of claims)", false)]
    public void TheOldEditionCheck_CatchesAlternativesAndOnlyThose(string regulation, bool expected)
    {
        Assert.Equal(expected, OldEditionAlternative.IsMatch(regulation));
    }

    // Checked against the wording of each rule on asa.org.uk.
    [Theory]
    [InlineData("ASA-1", "11.1")]   // the basis of an environmental claim must be clear
    [InlineData("ASA-2", "11.3")]   // absolute claims need a high level of substantiation
    [InlineData("ASA-5", "11.1")]
    [InlineData("ASA-10", "11.4")]  // full life cycle
    [InlineData("ASA-16", "3.3")]   // must not omit material information
    [InlineData("ASA-19", "11.5")]  // no suggesting universal acceptance where opinion is divided
    [InlineData("ASA-20", "11.6")]  // no implying a change to improve a product that never had a harmful effect
    [InlineData("ASA-21", "11.7")]  // no highlighting a benefit that comes from a legal obligation competitors share
    public void EachNumberedAsaRule_CitesTheCapRuleItIsAbout(string id, string rule)
    {
        Assert.Equal(new[] { rule }, CitedCapRules(RegulationById("asa_rules.json")[id]));
    }

    [Theory]
    [InlineData("FASH-1")]   // organic cotton
    [InlineData("FASH-5")]   // "sustainable" cashmere or wool
    public void AClaimThatMustBeSubstantiated_CitesTheSubstantiationRule(string id)
    {
        var regulation = RegulationById("fashion_rules.json")[id];

        Assert.Contains("Rule 3.7", regulation);
        Assert.DoesNotContain("Rule 11.1", regulation);
    }

    [Fact]
    public void ATruthfulAndAccurateClaim_CitesTheMisleadingAdvertisingRule()
    {
        Assert.Contains("Rule 3.1", RegulationById("fashion_rules.json")["FASH-2"]);
    }

    [Fact]
    public void FutureCommitments_DoNotCiteACapRuleThatIsAboutAbsoluteClaims()
    {
        var regulation = RegulationById("fashion_rules.json")["FASH-20"];

        Assert.Empty(CitedCapRules(regulation));
        Assert.Contains("Future commitments", regulation);
    }
}
