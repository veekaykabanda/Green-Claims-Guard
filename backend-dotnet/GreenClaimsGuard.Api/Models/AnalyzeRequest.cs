namespace GreenClaimsGuard.Api.Models;

public class AnalyzeRequest
{
    public string? Text { get; set; }
    public string? Industry { get; set; }
    public bool RulesOnly { get; set; } = false;

    // only a manual check (button click, submit, upload) gets logged, anything else is just a live typing check that's not logged
    public string? Trigger { get; set; }

    // the product this belongs to, once it exists, so the ledger can attribute the check to it
    public Guid? ProductId { get; set; }

    // name as it's being typed, checked like the description before it's saved
    public string? ProductName { get; set; }

    // category, subcategory and tags get checked too, since a label like "Eco" can itself be a claim
    public string? ProductCategory { get; set; }
    public string? ProductSubcategory { get; set; }
    public string? ProductTags { get; set; }

    // UK or EU, defaults to UK, picks which rules the copy is checked against
    public string? Market { get; set; }
    public List<IssueDecisionInput>? IssueDecisions { get; set; }
    public ProductFacts? ProductFacts { get; set; }
}

public class ProductFacts
{
    public string? MaterialComposition { get; set; }
    public List<string>? CertificationsHeld { get; set; }
    public string? AdditionalFacts { get; set; }
}
