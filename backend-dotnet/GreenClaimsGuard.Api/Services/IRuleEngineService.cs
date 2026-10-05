using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface IRuleEngineService
{
    // id for exactly which rule set produced a result ("UK-"/"EU-" plus a hash of the rule files). changes whenever the rules change
    string RulesVersionFor(string market);

    // How many rules the market's checks have loaded. Zero means the rules failed to load.
    int RuleCountFor(string market);

    // Checks the text against the rules of one market ("UK" or "EU"). UK unless told otherwise.
    List<RuleFinding> Analyze(string text, string market = Markets.Uk);

    // the rule sets for a market, exactly as loaded. guidance pages are built from these too so what's enforced matches what people are told
    IReadOnlyList<RuleSet> RuleSetsFor(string market);
}
