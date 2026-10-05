namespace GreenClaimsGuard.Api.Models;

public class AnalyzeResponse
{
    public string OverallStatus { get; set; } = ComplianceStatus.ChangesRequired;
    public string OverallRisk { get; set; } = "Low";
    public string TrafficLight { get; set; } = "\U0001F7E2";
    public int ComplianceScore { get; set; } = 100;
    public int TotalIssues { get; set; }
    public List<GroupedFinding> GroupedFindings { get; set; } = new();
    public List<RuleFinding> RuleFindings { get; set; } = new();
    public string AiExplanation { get; set; } = "";
    public string SuggestedRewrite { get; set; } = "";
    public List<Reference> References { get; set; } = new();
    public List<RequiredDocument> RequiredDocuments { get; set; } = new();

    // GroupedFindings are the "issues", the fields below say what was checked and what's allowed
    public string RulesStatus { get; set; } = EngineStatus.Ok;
    public string AiStatus { get; set; } = EngineStatus.Skipped;
    public string DbStatus { get; set; } = EngineStatus.NotChecked;
    public string FactsStatus { get; set; } = Models.FactsStatus.NotApplicable;
    public string Market { get; set; } = "UK";
    public string RulesVersion { get; set; } = "";
    // issues still blocking submission, critical ones still in the text plus any without a decision
    public int OpenIssueCount { get; set; }
    public bool SubmitAllowed { get; set; }
    public List<string> SubmitBlockingReasons { get; set; } = new();
    public bool PublishAllowed { get; set; }
    public List<string> BlockingReasons { get; set; } = new();

    // set when the writer kept a critical issue with a reason, submission's fine but publishing needs editor override
    public bool NeedsOverride { get; set; }
}

public class RequiredDocument
{
    public string ClaimType { get; set; } = "";
    public string DocumentName { get; set; } = "";
    public string Reason { get; set; } = "";
}
