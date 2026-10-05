using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface IAggregationService
{
    AnalyzeResponse Aggregate(List<RuleFinding> ruleFindings, LlmResult llmOutput, string sourceText);
}
