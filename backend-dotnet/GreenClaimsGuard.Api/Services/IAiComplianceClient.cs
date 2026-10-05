using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// only place the app talks to the AI for compliance checks, so tests can fake it and we can swap providers
public interface IAiComplianceClient
{
    Task<AiCheckResult> CheckAsync(AiCheckRequest request, CancellationToken cancellationToken);
}

public class AiCheckRequest
{
    public string Text { get; init; } = "";
    public string? Industry { get; init; }
    public string Market { get; init; } = Models.Markets.Uk;
    public List<AiFlaggedSentence> FlaggedSentences { get; init; } = new();

    // senior editor verifies these. only facts the AI is allowed to treat as true
    public VerifiedFacts? VerifiedFacts { get; init; }

    // copywriter's own notes, just context. not verified, AI can't treat it as fact
    public ProductFacts? WriterNotes { get; init; }

    public EvidenceFields? Evidence { get; init; }
}

public class AiFlaggedSentence
{
    public int Id { get; init; }
    public string Sentence { get; init; } = "";
    public string Categories { get; init; } = "";
    public string Severity { get; init; } = "";
}

// what the model sent back, parsed against a strict schema but not trusted yet
public class AiCheckResult
{
    // One of EngineStatus: Ok, NotConfigured, Timeout or Failed.
    public string Status { get; set; } = EngineStatus.Ok;

    public string RiskLevel { get; set; } = "none";
    public string Summary { get; set; } = "";
    public string CompliantRewrite { get; set; } = "";
    public List<AiCheckViolation> Violations { get; set; } = new();
    public List<AiSentenceRewrite> SentenceRewrites { get; set; } = new();
}

public class AiCheckViolation
{
    public string Phrase { get; set; } = "";
    public string RuleViolated { get; set; } = "";
    public string Severity { get; set; } = "WARNING";
    public string Replacement { get; set; } = "";
}

public class AiSentenceRewrite
{
    public int SentenceId { get; set; }
    public string Rewrite { get; set; } = "";
}
