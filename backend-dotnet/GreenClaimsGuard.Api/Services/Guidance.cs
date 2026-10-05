using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// guidance pages come from the same rule files the checker uses, so they can't drift apart. only the market intro text is written by hand
public static class Guidance
{
    private const int MaxExamples = 8;

    public static object Build(IRuleEngineService rules, string market)
    {
        var items = rules.RuleSetsFor(market)
            .SelectMany(set => set.Rules.Select(rule => new
            {
                id = rule.Id,
                category = rule.Category,
                severity = rule.Severity,
                regulation = rule.Regulation,
                explanation = rule.Explanation,
                source = set.Source,
                examples = rule.Patterns.Take(MaxExamples).ToArray(),
            }))
            .OrderBy(r => SeverityOrder(r.severity))
            .ThenBy(r => r.category)
            .ThenBy(r => r.id)
            .ToList();

        return new
        {
            market,
            rulesVersion = rules.RulesVersionFor(market),
            framework = market == Markets.Eu ? Eu : Uk,
            rules = items,
        };
    }

    private static int SeverityOrder(string severity) => severity.ToLowerInvariant() switch
    {
        "high" => 0,
        "medium" => 1,
        _ => 2,
    };

    private static readonly object Uk = new
    {
        title = "UK: the CMA Green Claims Code and the CAP Code",
        summary = "An environmental claim must be truthful and accurate, clear and unambiguous, not leave out or hide important information, make fair and meaningful comparisons, consider the full life cycle of the product, and be substantiated. Since 6 April 2025 the CMA can enforce consumer law directly, with fines of up to 10% of global turnover. In advertising, CAP Code rule 11.3 says absolute and comparative claims need a high level of substantiation, and rule 11.4 says a general claim is read as covering the full life cycle unless the advert clearly says otherwise.",
        note = (string?)null,
        links = new[]
        {
            new { name = "CMA Green Claims Code", url = "https://www.gov.uk/government/publications/green-claims-code-making-environmental-claims" },
            new { name = "ASA and CAP environmental claims guidance", url = "https://www.asa.org.uk/advice-online/environmental-claims.html" },
        },
    };

    private static readonly object Eu = new
    {
        title = "EU: the Empowering Consumers for the Green Transition (ECGT) Directive (EU) 2024/825",
        summary = "It amends the Unfair Commercial Practices Directive and applies from 27 September 2026. It bans generic environmental claims such as \"eco-friendly\", \"green\" or \"sustainable\" unless the trader can demonstrate recognised excellent environmental performance. It bans claims that a product is climate neutral, or has a reduced or positive climate impact, when they rest on offsetting. It bans sustainability labels that are not based on a certification scheme or set up by a public authority, and it requires claims about future environmental performance to rest on a detailed, verifiable plan. Product names and imagery can be claims too.",
        note = (string?)"The separate, proposed Green Claims Directive is not in force. It is not enforced here.",
        links = new[]
        {
            new { name = "Directive (EU) 2024/825", url = "https://eur-lex.europa.eu/eli/dir/2024/825/oj" },
            new { name = "Unfair Commercial Practices Directive 2005/29/EC", url = "https://eur-lex.europa.eu/eli/dir/2005/29/oj" },
        },
    };
}
