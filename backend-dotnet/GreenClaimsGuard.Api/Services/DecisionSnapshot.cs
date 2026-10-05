using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// client says which issue it decided about, but the server fills in what that issue actually was from its own check, so the record never trusts the client's wording
public static class DecisionSnapshot
{
    private const int MaxDecisions = 200;

    public static List<IssueDecisionInput> From(IEnumerable<IssueDecisionInput>? decisions, AnalyzeResponse evaluation)
    {
        var findingsById = evaluation.GroupedFindings
            .GroupBy(f => f.IssueId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var snapshot = new List<IssueDecisionInput>();
        foreach (var decision in (decisions ?? Enumerable.Empty<IssueDecisionInput>()).Take(MaxDecisions))
        {
            if (decision.UserDecision != ComplianceDecision.AppliedSuggestion
                && decision.UserDecision != ComplianceDecision.KeptOriginalWithJustification)
            {
                continue;
            }

            var issueId = (decision.IssueId ?? string.Empty).Trim();
            findingsById.TryGetValue(issueId, out var finding);

            snapshot.Add(new IssueDecisionInput
            {
                IssueId = issueId,
                Severity = finding is null ? decision.Severity : finding.Severity.ToUpperInvariant(),
                UserDecision = decision.UserDecision,
                UserJustification = decision.UserJustification,
                RequiresEvidence = decision.RequiresEvidence,
                EvidenceFields = decision.EvidenceFields,
                ResolvedSentenceSignature = decision.ResolvedSentenceSignature,
                // never taken from the client, this is null if the issue isn't in the text anymore
                Phrase = finding?.MatchedPatterns.FirstOrDefault(),
                Category = finding?.Category,
            });
        }

        return snapshot;
    }
}
