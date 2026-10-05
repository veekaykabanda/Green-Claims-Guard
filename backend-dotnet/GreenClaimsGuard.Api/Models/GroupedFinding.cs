namespace GreenClaimsGuard.Api.Models;

public class GroupedFinding
{
    public string IssueId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Regulation { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string IssueSummary { get; set; } = "";
    public string SuggestionText { get; set; } = "";
    public string? UserDecision { get; set; }
    public string? UserJustification { get; set; }
    public bool RequiresEvidence { get; set; }
    public EvidenceFields? EvidenceFields { get; set; }
    public string SentenceSignature { get; set; } = "";
    public bool IsResolved { get; set; }
    public List<string> MatchedPatterns { get; set; } = new();
    public List<string> RuleIds { get; set; } = new();
    public int Count { get; set; }
    public List<CaseReference> CaseReferences { get; set; } = new();

    // which field has the issue, name or description (both get checked the same way)
    public string Field { get; set; } = "description";
}
