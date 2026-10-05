using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface ILlmService
{
    // productFacts is the writer's own unverified notes. verifiedFacts comes from the db and is the only stuff the AI can treat as true
    Task<LlmResult> AnalyzeAsync(string text, List<RuleFinding> ruleFindings, string? industry = null, EvidenceFields? evidenceContext = null, ProductFacts? productFacts = null, VerifiedFacts? verifiedFacts = null, string market = Markets.Uk);
    Task<ProductFacts> ExtractDocumentFactsAsync(string documentText, string claimType);
    Task<DocumentValidationResult> ValidateDocumentClaimAsync(string documentText, string claimType, string? originalClaim = null);
}

public class LlmResult
{
    // One of EngineStatus: Ok, NotConfigured or Failed.
    public string Status { get; set; } = EngineStatus.Ok;
    public string AiExplanation { get; set; } = "";
    public string AiRiskLevel { get; set; } = "none"; // "none", "low", "medium", "high"
    public int AiIssueCount { get; set; }
    // backup rewrite for the worst issue, used when the per-sentence lookup fails
    public string TryThisWording { get; set; } = "";
    // rewrites for each sentence, keyed by the cleaned up sentence text
    public Dictionary<string, string> SentenceRewrites { get; set; } = new();
    // docs the brand needs to upload to back up their claims
    public List<RequiredDocument> RequiredDocuments { get; set; } = new();

    // extra issues the AI spotted that the rules missed. each phrase is checked to actually appear in the text with a usable replacement, but it's just advisory
    public List<AiViolation> AiViolations { get; set; } = new();

    // rewrites the AI made but we threw out because they made up numbers not in the copy or verified facts
    public int RejectedRewriteCount { get; set; }
}

public class AiViolation
{
    public string Phrase { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Replacement { get; set; } = "";
}
