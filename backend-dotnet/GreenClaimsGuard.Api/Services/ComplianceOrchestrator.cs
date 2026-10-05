using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public class ComplianceOrchestrator : IComplianceOrchestrator
{
    public const string FactsMismatchCategory = "product_facts_mismatch";
    public const string AiDetectedCategory = "ai_detected";

    // writer can keep a critical issue only if they say why, in at least this many characters
    public const int MinCriticalKeepReason = 15;

    private const double MaterialSimilarityThreshold = 0.85;
    private const int MaxAiIssues = 5;

    private readonly IRuleEngineService _rules;
    private readonly ILlmService _llm;
    private readonly IAggregationService _aggregation;
    private readonly IDbService _db;
    private readonly IProductFactsProvider _facts;
    private readonly ILogger<ComplianceOrchestrator> _logger;

    public ComplianceOrchestrator(
        IRuleEngineService rules,
        ILlmService llm,
        IAggregationService aggregation,
        IDbService db,
        IProductFactsProvider facts,
        ILogger<ComplianceOrchestrator> logger)
    {
        _rules = rules;
        _llm = llm;
        _aggregation = aggregation;
        _db = db;
        _facts = facts;
        _logger = logger;
    }

    public async Task<AnalyzeResponse> EvaluateAsync(ComplianceRequest request)
    {
        var text = (request.Text ?? string.Empty).Trim();
        var decisions = request.Decisions ?? new List<IssueDecisionInput>();
        var market = Markets.TryNormalise(request.Market, out var normalised) ? normalised : Markets.Uk;

        // 1. rules always run first and the AI can never override them
        var rulesStatus = EngineStatus.Ok;
        List<RuleFinding> ruleFindings;
        try
        {
            ruleFindings = _rules.Analyze(text, market);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rule engine failed");
            ruleFindings = new List<RuleFinding>();
            rulesStatus = EngineStatus.Failed;
        }

        // 2. load verified facts for this product, if it has any yet
        var factsLoad = await _facts.LoadAsync(request.ProductId);

        // 3. AI always runs on submit and publish, only a live typing check can skip it
        var runAi = request.Action != ComplianceAction.Analyze || !request.RulesOnly;
        var aiStatus = EngineStatus.Skipped;
        var llmOutput = new LlmResult();
        if (runAi)
        {
            var evidenceContext = decisions
                .Where(d => d.UserDecision == ComplianceDecision.KeptOriginalWithJustification
                            && IsEvidenceComplete(d.EvidenceFields))
                .Select(d => d.EvidenceFields!)
                .FirstOrDefault();

            try
            {
                llmOutput = await _llm.AnalyzeAsync(text, ruleFindings, request.Industry, evidenceContext, request.ProductFacts, factsLoad.Facts, market);
                aiStatus = llmOutput.Status;

                // worth knowing if the AI keeps inventing numbers, but whatever it makes up never reaches a user
                if (llmOutput.RejectedRewriteCount > 0)
                {
                    _logger.LogWarning("Discarded {Count} AI suggestion(s) that stated numbers not in the copy or the verified facts", llmOutput.RejectedRewriteCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI check failed");
                llmOutput = new LlmResult { Status = EngineStatus.Failed };
                aiStatus = EngineStatus.Failed;
            }
        }

        // 4. merge everything into issues
        var result = _aggregation.Aggregate(ruleFindings, llmOutput, text);

        var factsCheck = ProductFactsChecker.Check(text, factsLoad.Facts);
        AddFactsMismatchIssues(result, factsCheck, text, market);
        AddAiIssues(result, llmOutput, text, market);

        // product name and the details fields (category/subcategory/tags) get the same rules and facts check as the description, but never an AI rewrite. making up a replacement for a few words would do more harm than just letting the writer edit it
        var auxiliaryFacts = 0;
        rulesStatus = CheckAuxiliaryField(result, request.ProductName, "name", "name-", market, factsLoad, rulesStatus, ref auxiliaryFacts);
        rulesStatus = CheckAuxiliaryField(result, request.ProductCategory, "category", "category-", market, factsLoad, rulesStatus, ref auxiliaryFacts);
        rulesStatus = CheckAuxiliaryField(result, request.ProductSubcategory, "subcategory", "subcategory-", market, factsLoad, rulesStatus, ref auxiliaryFacts);
        rulesStatus = CheckAuxiliaryField(result, request.ProductTags, "tags", "tags-", market, factsLoad, rulesStatus, ref auxiliaryFacts);
        var nameFactsCheck = new FactsCheckResult { CheckableClaimCount = auxiliaryFacts };

        ApplyMarketPresentation(result, market);
        SortBySeverity(result);

        // 5. apply the user's recorded decisions
        ApplyDecisions(result, decisions);

        // no findings at all counts as "ready", same as every finding being resolved. before this, clean copy with zero findings wrongly stayed stuck on "changes required"
        result.OverallStatus = result.GroupedFindings.All(f => f.IsResolved)
            ? ComplianceStatus.ReadyToPublishSubjectToReview
            : ComplianceStatus.ChangesRequired;

        // 6. What ran, against which rules, and what the server will allow.
        result.RulesStatus = rulesStatus;
        result.AiStatus = aiStatus;
        result.FactsStatus = factsLoad.Status;
        result.Market = market;
        result.RulesVersion = _rules.RulesVersionFor(market);
        result.DbStatus = await GetDbStatusAsync(request.Action);

        var submitReasons = new List<string>();
        var keptCritical = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            submitReasons.Add("The description is empty.");
        }
        if (rulesStatus != EngineStatus.Ok)
        {
            submitReasons.Add("The rules check could not run, so this text has not been checked.");
        }
        foreach (var issue in result.GroupedFindings)
        {
            // the AI isn't consistent from one run to the next, so it can suggest stuff but never block. only rules and verified facts can stop a submission
            if (issue.Category == AiDetectedCategory) continue;

            var phrase = issue.MatchedPatterns.FirstOrDefault() ?? issue.Category;
            var topic = issue.Category.Replace('_', ' ');
            var where = issue.Field switch
            {
                "name" => "the product name",
                "category" => "the product category",
                "subcategory" => "the product subcategory",
                "tags" => "the product tags",
                _ => "the text"
            };

            // a critical (high) issue stays open until the checks stop finding it. the writer can fix it or keep it with a real reason, which lets submission through but blocks publish until a senior editor overrides it. except a facts contradiction, that's just false and can never be kept
            if (IsHighSeverity(issue.Severity))
            {
                if (issue.Category == FactsMismatchCategory)
                {
                    submitReasons.Add($"Critical: {issue.Explanation} Correct the copy; it cannot be kept.");
                }
                else if (issue.IsResolved && issue.UserDecision == ComplianceDecision.KeptOriginalWithJustification)
                {
                    keptCritical.Add($"Kept by the writer in {where}: \"{phrase}\" ({topic}). A Senior Editor must override with their own written reason before this can be published.");
                }
                else
                {
                    submitReasons.Add($"Critical issue still in {where}: \"{phrase}\" ({topic}). Fix it, or keep it with a written reason of at least {MinCriticalKeepReason} characters.");
                }
            }
            else if (!issue.IsResolved)
            {
                submitReasons.Add($"Needs a decision: \"{phrase}\" ({topic}). Apply the rewrite, or keep it with a justification.");
            }
        }

        result.OpenIssueCount = result.GroupedFindings.Count(f =>
            f.Category != AiDetectedCategory && (IsHighSeverity(f.Severity) || !f.IsResolved));
        result.SubmitBlockingReasons = submitReasons;
        result.SubmitAllowed = submitReasons.Count == 0;

        // publishing is stricter, the AI check must have run on this exact text and the facts it's checked against must actually be on file
        var publishReasons = new List<string>(submitReasons);
        publishReasons.AddRange(keptCritical);
        result.NeedsOverride = keptCritical.Count > 0;
        if (aiStatus != EngineStatus.Ok)
        {
            publishReasons.Add(aiStatus switch
            {
                EngineStatus.Skipped => "The AI check has not run for this version of the text.",
                EngineStatus.NotConfigured => "The AI check is not configured, so it has not run for this version of the text.",
                EngineStatus.Timeout => "The AI check timed out, so it has not run for this version of the text.",
                _ => "The AI check failed, so it has not run for this version of the text."
            });
        }
        if (factsLoad.Status == FactsStatus.Unavailable || result.DbStatus is EngineStatus.Unavailable or EngineStatus.NotConfigured)
        {
            publishReasons.Add("Product facts could not be loaded because the database is unavailable.");
        }
        else if (factsLoad.Status != FactsStatus.Loaded && factsCheck.CheckableClaimCount + nameFactsCheck.CheckableClaimCount > 0)
        {
            publishReasons.Add("The copy makes composition, certification or origin claims that cannot be verified, because no verified product facts are on file.");
        }

        result.BlockingReasons = publishReasons;
        result.PublishAllowed = publishReasons.Count == 0;

        return result;
    }

    private async Task<string> GetDbStatusAsync(ComplianceAction action)
    {
        if (!_db.IsConfigured) return EngineStatus.NotConfigured;

        // a live typing check fires constantly, don't hit the database for it
        if (action == ComplianceAction.Analyze) return EngineStatus.NotChecked;

        return await _db.IsAvailableAsync() ? EngineStatus.Ok : EngineStatus.Unavailable;
    }

    // a claim that contradicts the verified facts is a critical issue, found by comparing copy to facts directly, no AI involved
    private static void AddFactsMismatchIssues(AnalyzeResponse result, FactsCheckResult check, string text, string market, string idPrefix = "", string field = "description")
    {
        var index = 1;
        foreach (var mismatch in check.Mismatches)
        {
            result.GroupedFindings.Add(new GroupedFinding
            {
                IssueId = $"{idPrefix}facts-mismatch-{index++:00}",
                Field = field,
                Category = FactsMismatchCategory,
                Severity = "high",
                Regulation = market == Markets.Eu
                    ? "Directive 2005/29/EC Article 6(1) (misleading actions): information about a product's composition or certification must be true."
                    : "CMA Green Claims Code: claims must be truthful and accurate. CAP Code rule 3.1 (misleading advertising).",
                Explanation = mismatch.Message,
                IssueSummary = mismatch.Message,
                SuggestionText = mismatch.CorrectedSentence,
                MatchedPatterns = new List<string> { mismatch.Phrase },
                RuleIds = new List<string> { "FACT-1" },
                Count = 1,
                SentenceSignature = SentenceSignature(text, mismatch.Phrase),
                IsResolved = false
            });
        }

        if (check.Mismatches.Count == 0) return;

        result.OverallRisk = "High";
        result.TrafficLight = "\U0001F534";
        result.ComplianceScore = Math.Max(5, result.ComplianceScore - 12 * check.Mismatches.Count);
        result.TotalIssues += check.Mismatches.Count;
    }

    // runs rules and facts check on one short field (name or a details field), same as the description but no AI rewrite since it's a few words not a paragraph
    private string CheckAuxiliaryField(
        AnalyzeResponse result, string? rawValue, string field, string idPrefix, string market,
        FactsLoadResult factsLoad, string rulesStatus, ref int checkableFactCount)
    {
        var value = (rawValue ?? string.Empty).Trim();
        if (value.Length == 0) return rulesStatus;

        List<RuleFinding> findings;
        try
        {
            findings = _rules.Analyze(value, market);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rule engine failed on the product {Field}", field);
            return EngineStatus.Failed;
        }

        AddFieldIssues(result, findings, value, field, idPrefix);

        var factsCheck = ProductFactsChecker.Check(value, factsLoad.Facts);
        AddFactsMismatchIssues(result, factsCheck, value, market, idPrefix, field);
        checkableFactCount += factsCheck.CheckableClaimCount;

        return rulesStatus;
    }

    // turns one field's rule findings into issues with their own ids, so a decision about it never gets mixed up with the description. no rewrite offered, writer edits it by hand
    private void AddFieldIssues(AnalyzeResponse result, List<RuleFinding> fieldFindings, string value, string field, string idPrefix)
    {
        var aggregated = _aggregation.Aggregate(fieldFindings, new LlmResult(), value);
        foreach (var finding in aggregated.GroupedFindings)
        {
            finding.IssueId = idPrefix + finding.IssueId;
            finding.Field = field;
            finding.SuggestionText = string.Empty;
            result.GroupedFindings.Add(finding);
        }

        if (aggregated.GroupedFindings.Count == 0) return;

        result.TotalIssues += aggregated.GroupedFindings.Count;
        if (aggregated.GroupedFindings.Any(f => IsHighSeverity(f.Severity)))
        {
            result.OverallRisk = "High";
            result.TrafficLight = "\U0001F534";
        }
    }
    // AI might catch something the rules missed, but only what can be checked survives (phrase must really be in the text with a real replacement). capped at medium and never blocks
    private static void AddAiIssues(AnalyzeResponse result, LlmResult llm, string text, string market)
    {
        var added = 0;
        foreach (var violation in llm.AiViolations)
        {
            if (added >= MaxAiIssues) break;

            var signature = SentenceSignature(text, violation.Phrase);
            if (string.IsNullOrWhiteSpace(signature)) continue;
            if (result.GroupedFindings.Any(f => f.SentenceSignature == signature)) continue;

            result.GroupedFindings.Add(new GroupedFinding
            {
                IssueId = "ai-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(signature)))[..8].ToLowerInvariant(),
                Category = AiDetectedCategory,
                Severity = "medium",
                Regulation = market == Markets.Eu
                    ? "Directive 2005/29/EC as amended by Directive (EU) 2024/825 (identified by the AI check)"
                    : "CMA Green Claims Code / CAP Code (identified by the AI check)",
                Explanation = violation.Rule,
                IssueSummary = violation.Rule,
                SuggestionText = violation.Replacement,
                MatchedPatterns = new List<string> { violation.Phrase },
                RuleIds = new List<string> { "AI" },
                Count = 1,
                SentenceSignature = signature,
                IsResolved = false
            });
            added++;
        }
    }

    // aggregation only knows UK sources and UK precedent cases, wrong to cite for the EU, so swap in EU legal sources and drop the UK cases
    private static void ApplyMarketPresentation(AnalyzeResponse result, string market)
    {
        if (market != Markets.Eu) return;

        result.References = new List<Reference>
        {
            new()
            {
                Name = "Directive (EU) 2024/825 (Empowering Consumers for the Green Transition)",
                Url = "https://eur-lex.europa.eu/eli/dir/2024/825/oj"
            },
            new()
            {
                Name = "Unfair Commercial Practices Directive 2005/29/EC",
                Url = "https://eur-lex.europa.eu/eli/dir/2005/29/oj"
            }
        };

        foreach (var finding in result.GroupedFindings)
        {
            finding.CaseReferences = new();
        }
        foreach (var document in result.RequiredDocuments)
        {
            document.Reason = document.Reason.Replace(" (CMA principle 3)", string.Empty);
        }
    }
    private static void SortBySeverity(AnalyzeResponse result)
    {
        var order = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
        result.GroupedFindings = result.GroupedFindings
            .Select((finding, position) => (finding, position))
            .OrderBy(x => order.GetValueOrDefault(x.finding.Severity, 2))
            .ThenBy(x => x.position)
            .Select(x => x.finding)
            .ToList();
    }

    private static string SentenceSignature(string text, string phrase)
    {
        // stops at line breaks and bullet points (· or •) too, so ASOS-style copy never gets merged into one "sentence" spanning unrelated bits
        var sentence = Regex.Matches(text ?? string.Empty, @"[^.!?\n·•]+[.!?]*")
            .Select(m => m.Value.Trim())
            .FirstOrDefault(s => s.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        return NormalizeForSimilarity(sentence);
    }

    // matches each decision to the issue it was about and marks it resolved if valid. same behaviour as when this lived in the analyze endpoint
    private static void ApplyDecisions(AnalyzeResponse result, List<IssueDecisionInput> incomingDecisions)
    {
        var decisionsByIssueId = incomingDecisions
            .Where(d => !string.IsNullOrWhiteSpace(d.IssueId))
            .GroupBy(d => d.IssueId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var finding in result.GroupedFindings)
        {
            if (!decisionsByIssueId.TryGetValue(finding.IssueId, out var decision))
            {
                continue;
            }

            var userDecision = (decision.UserDecision ?? string.Empty).Trim();
            if (!IsDecisionValueValid(userDecision))
            {
                continue;
            }

            var requiresEvidence = finding.RequiresEvidence || decision.RequiresEvidence;
            var evidenceFields = decision.EvidenceFields ?? finding.EvidenceFields;
            // applying the AI suggestion always resolves the issue, evidence is only needed if you keep a reduction or comparison claim
            var evidenceComplete = !requiresEvidence
                || userDecision == ComplianceDecision.AppliedSuggestion
                || (userDecision == ComplianceDecision.KeptOriginalWithJustification && IsEvidenceComplete(evidenceFields));
            var hasJustification = userDecision != ComplianceDecision.KeptOriginalWithJustification
                                   || !string.IsNullOrWhiteSpace(decision.UserJustification);

            var highResolved = !IsHighSeverity(finding.Severity)
                               || userDecision == ComplianceDecision.AppliedSuggestion
                               || (userDecision == ComplianceDecision.KeptOriginalWithJustification
                                   && finding.Category != FactsMismatchCategory
                                   && (decision.UserJustification ?? string.Empty).Trim().Length >= MinCriticalKeepReason);

            var currentSignature = NormalizeForSimilarity(finding.SentenceSignature);
            var resolvedSignature = NormalizeForSimilarity(decision.ResolvedSentenceSignature);
            var materiallySame = string.IsNullOrWhiteSpace(currentSignature)
                                 || string.IsNullOrWhiteSpace(resolvedSignature)
                                 || CalculateSimilarity(currentSignature, resolvedSignature) >= MaterialSimilarityThreshold;

            if (!(hasJustification && evidenceComplete && highResolved && materiallySame))
            {
                continue;
            }

            finding.UserDecision = userDecision;
            finding.UserJustification = decision.UserJustification;
            finding.RequiresEvidence = requiresEvidence;
            finding.EvidenceFields = evidenceFields;
            finding.IsResolved = true;
        }
    }

    private static bool IsDecisionValueValid(string? decision) =>
        decision == ComplianceDecision.AppliedSuggestion
        || decision == ComplianceDecision.KeptOriginalWithJustification;

    private static bool IsEvidenceComplete(EvidenceFields? evidence)
    {
        if (evidence is null) return false;

        return !string.IsNullOrWhiteSpace(evidence.Baseline)
               && evidence.ReductionPercentage.HasValue
               && evidence.ReductionPercentage.Value > 0
               && !string.IsNullOrWhiteSpace(evidence.Timeframe)
               && !string.IsNullOrWhiteSpace(evidence.EvidenceReference);
    }

    private static bool IsHighSeverity(string? severity) =>
        string.Equals((severity ?? string.Empty).Trim(), "high", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForSimilarity(string? value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"[^a-zA-Z0-9\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static double CalculateSimilarity(string? left, string? right)
    {
        var normalizedLeft = NormalizeForSimilarity(left);
        var normalizedRight = NormalizeForSimilarity(right);

        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            return 0;

        if (normalizedLeft == normalizedRight)
            return 1;

        var leftTokens = normalizedLeft.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = normalizedRight.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (leftTokens.Length == 0 || rightTokens.Length == 0)
            return 0;

        var leftCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in leftTokens)
        {
            leftCounts[token] = leftCounts.GetValueOrDefault(token, 0) + 1;
        }

        var intersection = 0;
        foreach (var token in rightTokens)
        {
            var count = leftCounts.GetValueOrDefault(token, 0);
            if (count > 0)
            {
                intersection++;
                leftCounts[token] = count - 1;
            }
        }

        return (2.0 * intersection) / (leftTokens.Length + rightTokens.Length);
    }
}
