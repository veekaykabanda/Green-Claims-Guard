using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;

namespace GreenClaimsGuard.Api.Tests;

// fakes the AI so tests don't make a real billable call, and we can control how it responds
public class FakeLlmService : ILlmService
{
    public string Status { get; set; } = EngineStatus.Ok;

    public Task<LlmResult> AnalyzeAsync(
        string text,
        List<RuleFinding> ruleFindings,
        string? industry = null,
        EvidenceFields? evidenceContext = null,
        ProductFacts? productFacts = null,
        VerifiedFacts? verifiedFacts = null,
        string market = Markets.Uk) =>
        Task.FromResult(new LlmResult
        {
            Status = Status,
            AiExplanation = Status == EngineStatus.Ok ? "Fake AI explanation." : $"Fake AI is {Status}."
        });

    public Task<ProductFacts> ExtractDocumentFactsAsync(string documentText, string claimType) =>
        Task.FromResult(new ProductFacts());

    public Task<DocumentValidationResult> ValidateDocumentClaimAsync(string documentText, string claimType, string? originalClaim = null) =>
        Task.FromResult(new DocumentValidationResult { ValidationStatus = "VALIDATED" });

    // temporarily changes the AI's status until the thing you get back is disposed
    public IDisposable Using(string status)
    {
        var previous = Status;
        Status = status;
        return new Restore(() => Status = previous);
    }

    private sealed class Restore : IDisposable
    {
        private readonly Action _restore;
        public Restore(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }
}

// fakes just the OpenAI client, so the real LlmService and AiOutputGuard still run in tests
public class FakeAiComplianceClient : IAiComplianceClient
{
    public Func<AiCheckRequest, AiCheckResult> Reply { get; set; } = _ => new AiCheckResult { Status = EngineStatus.Ok };
    public AiCheckRequest? LastRequest { get; private set; }

    public Task<AiCheckResult> CheckAsync(AiCheckRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(Reply(request));
    }
}

// fake db where the test controls if it's available, used to test the orchestrator directly
public class FakeDbService : IDbService
{
    public bool IsConfigured { get; set; } = true;
    public bool Available { get; set; } = true;

    public Task<bool> IsAvailableAsync() => Task.FromResult(IsConfigured && Available);
    public Task LogAnalysisAsync(string text, AnalyzeResponse result, List<RuleFinding> ruleFindings) => Task.CompletedTask;
    public Task<DbStatus> CheckStatusAsync() => Task.FromResult(new DbStatus { Configured = IsConfigured, Connected = Available });
}
