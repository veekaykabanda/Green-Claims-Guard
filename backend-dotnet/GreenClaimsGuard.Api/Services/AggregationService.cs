using GreenClaimsGuard.Api.Models;
using System.Text.RegularExpressions;

namespace GreenClaimsGuard.Api.Services;

public class AggregationService : IAggregationService
{
    private readonly ICaseReferenceService _caseReferenceService;

    public AggregationService(ICaseReferenceService caseReferenceService)
    {
        _caseReferenceService = caseReferenceService;
    }

    public AnalyzeResponse Aggregate(List<RuleFinding> ruleFindings, LlmResult llmOutput, string sourceText)
    {
        // Start from rule-engine severity
        var hasHigh = ruleFindings.Any(f => f.Severity == "high");
        var hasMedium = ruleFindings.Any(f => f.Severity == "medium");

        string overallRisk;
        string trafficLight;

        if (hasHigh)
        {
            overallRisk = "High";
            trafficLight = "\U0001F534"; // Red circle
        }
        else if (hasMedium)
        {
            overallRisk = "Medium";
            trafficLight = "\U0001F7E1"; // Yellow circle
        }
        else
        {
            overallRisk = "Low";
            trafficLight = "\U0001F7E2"; // Green circle
        }

        // Elevate risk if AI detected issues the rule engine missed
        var aiRisk = llmOutput.AiRiskLevel?.ToLowerInvariant() ?? "none";
        if (aiRisk == "high" && overallRisk != "High")
        {
            overallRisk = "High";
            trafficLight = "\U0001F534";
        }
        else if (aiRisk == "medium" && overallRisk == "Low")
        {
            overallRisk = "Medium";
            trafficLight = "\U0001F7E1";
        }
        else if (aiRisk == "low" && overallRisk == "Low" && ruleFindings.Count == 0 && llmOutput.AiIssueCount > 0)
        {
            overallRisk = "Low";
            trafficLight = "\U0001F7E2";
        }

        var complianceScore = CalculateComplianceScore(ruleFindings, llmOutput);
        var totalIssues = Math.Max(ruleFindings.Count, llmOutput.AiIssueCount);
        var groupedFindings = GroupByCategory(ruleFindings, sourceText, _caseReferenceService, llmOutput.SentenceRewrites);

        var references = new List<Reference>
        {
            new()
            {
                Name = "CMA Green Claims Code",
                Url = "https://www.gov.uk/government/publications/green-claims-code-making-environmental-claims"
            },
            new()
            {
                Name = "ASA Environmental Guidance",
                Url = "https://www.asa.org.uk/advice-online/environmental-claims.html"
            }
        };

        return new AnalyzeResponse
        {
            OverallStatus = totalIssues == 0
                ? ComplianceStatus.ReadyToPublishSubjectToReview
                : ComplianceStatus.ChangesRequired,
            OverallRisk = overallRisk,
            TrafficLight = trafficLight,
            ComplianceScore = complianceScore,
            TotalIssues = totalIssues,
            GroupedFindings = groupedFindings,
            RuleFindings = ruleFindings,
            AiExplanation = llmOutput.AiExplanation ?? "",
            SuggestedRewrite = "",
            References = references,
            RequiredDocuments = DetermineRequiredDocuments(ruleFindings)
        };
    }

    // hardcoded mapping from rule findings to evidence docs, not AI, so it always works even with no token budget
    private static List<RequiredDocument> DetermineRequiredDocuments(List<RuleFinding> ruleFindings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RequiredDocument>();

        void TryAdd(string claimType, string documentName, string reason)
        {
            if (seen.Add(claimType))
                result.Add(new RequiredDocument { ClaimType = claimType, DocumentName = documentName, Reason = reason });
        }

        foreach (var finding in ruleFindings)
        {
            var cat = (finding.Category ?? "").ToLowerInvariant().Replace("_", " ");
            var pattern = (finding.MatchedPattern ?? "").ToLowerInvariant();
            var ruleId = (finding.RuleId ?? "").ToUpperInvariant().Trim();

            // organic cotton, FASH-1 and FASH-14 (gots_certification_required)
            if (cat.Contains("organic") || cat.Contains("gots") || pattern.Contains("organic cotton") || ruleId == "FASH-1" || ruleId == "FASH-14")
                TryAdd("organic_cotton", "Organic Cotton Certification", "GOTS or Soil Association certificate required for organic cotton claims (CMA principle 3)");

            // recycled content, FASH-3
            if (cat.Contains("recycled") || pattern.Contains("recycled") || ruleId == "FASH-3")
                TryAdd("recycled_content", "Recycled Content Certificate", "GRS (Global Recycled Standard) or equivalent supplier evidence of recycled content percentage");

            // carbon neutral or net zero, FASH-9 and FASH-18 (local_uk_made_carbon)
            if (cat.Contains("carbon") || pattern.Contains("carbon neutral") || pattern.Contains("net zero") || pattern.Contains("carbon negative") || ruleId == "FASH-9" || ruleId == "FASH-18")
                TryAdd("carbon_neutral", "Carbon Neutrality Verification", "ISO 14068 (Jan 2025+) or PAS 2060 certificate with scope statement and third-party verification");

            // water reduction, FASH-15 (waterless_dyeing_claims)
            if (cat.Contains("water") || pattern.Contains("water saving") || pattern.Contains("water reduction") || pattern.Contains("water use") || ruleId == "FASH-15")
                TryAdd("water_reduction", "Water Reduction Evidence", "Verified baseline and reduction figures with methodology, from a supplier audit or third-party measurement");

            // Zero waste
            if (cat.Contains("zero waste") || pattern.Contains("zero waste") || pattern.Contains("landfill free"))
                TryAdd("zero_waste", "Waste Diversion Evidence", "Verified waste audit or certification showing waste diversion rate and disposal method");

            // sustainable wool or cashmere, FASH-5
            if (cat.Contains("wool") || cat.Contains("cashmere") || pattern.Contains("sustainable wool") || pattern.Contains("sustainable cashmere") || ruleId == "FASH-5")
                TryAdd("sustainable_wool", "Responsible Wool Standard Certificate", "RWS certification or equivalent animal welfare and land management evidence");

            // vegan leather, FASH-4
            if (cat.Contains("vegan") || pattern.Contains("vegan leather") || ruleId == "FASH-4")
                TryAdd("vegan_leather", "Vegan Leather Material Evidence", "Material composition and lifecycle data required for environmental claims on vegan leather");
        }

        return result;
    }

    private static List<GroupedFinding> GroupByCategory(List<RuleFinding> ruleFindings, string sourceText, ICaseReferenceService caseReferenceService, Dictionary<string, string>? sentenceRewrites = null)
    {
        var grouped = new Dictionary<string, GroupedFinding>();

        foreach (var finding in ruleFindings)
        {
            var cat = finding.Category;

            if (!grouped.TryGetValue(cat, out var group))
            {
                group = new GroupedFinding
                {
                    Category = cat,
                    Severity = finding.Severity,
                    Regulation = finding.Regulation,
                    Explanation = finding.Explanation,
                    IssueSummary = BuildIssueSummary(cat, finding.Explanation),
                    SuggestionText = BuildSuggestionText(cat, finding.MatchedPattern),
                    RequiresEvidence = RequiresEvidence(cat),
                    EvidenceFields = RequiresEvidence(cat) ? new EvidenceFields() : null,
                    MatchedPatterns = new List<string>(),
                    RuleIds = new List<string>(),
                    Count = 0
                };
                grouped[cat] = group;
            }

            if (!group.MatchedPatterns.Contains(finding.MatchedPattern))
                group.MatchedPatterns.Add(finding.MatchedPattern);

            if (!group.RuleIds.Contains(finding.RuleId))
                group.RuleIds.Add(finding.RuleId);

            group.Count++;

            // Keep highest severity
            if (finding.Severity == "high")
                group.Severity = "high";
            else if (finding.Severity == "medium" && group.Severity != "high")
                group.Severity = "medium";
        }

        var result = grouped.Values.ToList();

        var severityOrder = new Dictionary<string, int>
        {
            ["high"] = 0,
            ["medium"] = 1,
            ["low"] = 2
        };

        result.Sort((a, b) =>
        {
            var aOrder = severityOrder.GetValueOrDefault(a.Severity, 2);
            var bOrder = severityOrder.GetValueOrDefault(b.Severity, 2);
            return aOrder.CompareTo(bOrder);
        });

        for (var i = 0; i < result.Count; i++)
        {
            var group = result[i];
            group.IssueId = BuildIssueId(group, i + 1);
            group.SentenceSignature = BuildSentenceSignature(sourceText, group.MatchedPatterns);
            group.IsResolved = false;

            // keep summaries short for marketers, no long policy text blocks in the UI
            if (string.IsNullOrWhiteSpace(group.IssueSummary))
            {
                group.IssueSummary = BuildIssueSummary(group.Category, group.Explanation);
            }

            // first choice: the AI's rewrite for this exact sentence, safe to drop in for just this finding
            if (sentenceRewrites != null
                && !string.IsNullOrWhiteSpace(group.SentenceSignature)
                && sentenceRewrites.TryGetValue(group.SentenceSignature, out var sentenceRewrite)
                && !string.IsNullOrWhiteSpace(sentenceRewrite))
            {
                group.SuggestionText = sentenceRewrite;
            }
            // second choice: fallback rule-based suggestion. not using the whole-description rewrite here since it duplicated every bullet when spliced into one finding's spot (seen on ASOS-style bulleted copy)
            else if (string.IsNullOrWhiteSpace(group.SuggestionText))
            {
                group.SuggestionText = BuildSuggestionText(group.Category, group.MatchedPatterns.FirstOrDefault() ?? "");
            }

            // evidence form only shows for reduction or comparison claims where you keep your own wording, everything else gets fixed by the AI rewrite
            group.RequiresEvidence = RequiresEvidence(group.Category, group.Severity);
            if (group.RequiresEvidence && group.EvidenceFields is null)
            {
                group.EvidenceFields = new EvidenceFields();
            }
            else if (!group.RequiresEvidence)
            {
                group.EvidenceFields = null;
            }

            group.CaseReferences = caseReferenceService.GetCasesForRuleIds(group.RuleIds);
        }

        // removes duplicate sentences (list is already sorted high to medium to low, so the first one per sentence is the worst)
        var seenSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduplicated = new List<GroupedFinding>(result.Count);
        foreach (var finding in result)
        {
            var sig = finding.SentenceSignature;
            if (string.IsNullOrWhiteSpace(sig))
            {
                deduplicated.Add(finding);
                continue;
            }
            if (seenSignatures.Add(sig))
                deduplicated.Add(finding);
            // else a worse issue for this same sentence is already in the list, so drop this one
        }

        return deduplicated;
    }

    private static string BuildIssueId(GroupedFinding group, int index)
    {
        var baseText = string.IsNullOrWhiteSpace(group.Category) ? "issue" : group.Category;
        var slug = Regex.Replace(baseText.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "issue";
        }

        return $"{slug}-{index:00}";
    }

    private static string BuildSentenceSignature(string sourceText, List<string> matchedPatterns)
    {
        var sentence = ExtractMatchedSentence(sourceText, matchedPatterns);
        return NormalizeForSimilarity(sentence);
    }

    private static string ExtractMatchedSentence(string sourceText, List<string> matchedPatterns)
    {
        var source = (sourceText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        var primaryPattern = matchedPatterns
            .Select(p => (p ?? string.Empty).Trim())
            .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        if (!string.IsNullOrWhiteSpace(primaryPattern))
        {
            // tries sentence first (same as LlmService.FindLineForPattern), stops at bullet points too so ASOS-style copy doesn't get merged into one meaningless "sentence"
            var sentenceMatch = Regex.Matches(source, @"[^.!?\n·•]+[.!?]*")
                .Select(m => m.Value.Trim())
                .FirstOrDefault(s => s.Contains(primaryPattern, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(sentenceMatch))
                return sentenceMatch;

            // Fallback: line-level split
            var lineMatch = source.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Contains(primaryPattern, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(lineMatch))
                return lineMatch;
        }

        // Last resort: first non-empty line
        return source.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? source;
    }

    private static string NormalizeForSimilarity(string value)
    {
        var normalized = Regex.Replace(value ?? string.Empty, @"[^a-zA-Z0-9\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
        return normalized;
    }

    private static bool RequiresEvidence(string category, string severity = "low")
    {
        // evidence form only shows for reduction or comparison claims kept in your own wording (like FASH-15 water claims, which needs a baseline, percent and methodology). everything else gets fixed by the AI rewrite
        var key = (category ?? string.Empty).ToLowerInvariant().Replace("_", " ");
        return key.Contains("reduction") || key.Contains("compar") || key.Contains("water");
    }

    private static string BuildIssueSummary(string category, string explanation)
    {
        var key = (category ?? string.Empty).ToLowerInvariant();

        if (key.Contains("truthful") || key.Contains("clear") || key.Contains("greenwashing"))
        {
            return "Terms like 'eco-friendly' or 'sustainable' need clear meaning and evidence.";
        }

        if (key.Contains("reduction") || key.Contains("compar"))
        {
            return "State what is being reduced, from what baseline, by how much and over what period.";
        }

        if (key.Contains("absolute"))
        {
            return "Absolute claims need very strong evidence and clear scope.";
        }

        if (key.Contains("substantiated"))
        {
            return "This claim needs up-to-date evidence you can produce if challenged.";
        }

        if (key.Contains("omission"))
        {
            return "Key qualifiers should appear next to the claim so customers are not misled.";
        }

        var firstSentence = string.IsNullOrWhiteSpace(explanation)
            ? "Review this wording and make the claim clear, accurate and evidence-backed."
            : explanation.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        firstSentence = (firstSentence ?? "Review this wording for clarity and evidence.").Trim();
        return firstSentence.EndsWith('.') ? firstSentence : $"{firstSentence}.";
    }

    private static string BuildSuggestionText(string category, string matchedPattern)
    {
        var key = (category ?? string.Empty).ToLowerInvariant();
        var matched = (matchedPattern ?? string.Empty).Trim();
        var matchedLower = matched.ToLowerInvariant();

        if (key.Contains("reduction") || key.Contains("compar"))
        {
            return "Compared with our previous range, this product uses lower-impact production steps where measured and evidenced.";
        }

        if (key.Contains("carbon neutral")
            || key.Contains("no omission")
            || matchedLower.Contains("carbon neutral")
            || matchedLower.Contains("net zero")
            || matchedLower.Contains("carbon negative"))
        {
            return "Any carbon claim should clearly state scope, method and offset details with current evidence.";
        }

        if (key.Contains("recycled") || matchedLower.Contains("recycled"))
        {
            return "This product includes recycled content in specified components, with percentages and scope stated clearly.";
        }

        if (key.Contains("absolute"))
        {
            return "This claim should avoid absolute wording unless full lifecycle evidence is available and clearly described.";
        }

        if (key.Contains("substantiated"))
        {
            return "This environmental claim should be supported by current supplier evidence that can be shared if requested.";
        }

        if (key.Contains("omission"))
        {
            return "Add clear scope, method and limits next to this claim so customers can understand exactly what it covers.";
        }

        if (key.Contains("truthful") || key.Contains("clear") || key.Contains("greenwashing"))
        {
            return "Replace broad terms with specific wording and evidence so the claim is clear, accurate and not misleading.";
        }

        if (!string.IsNullOrWhiteSpace(matchedLower))
        {
            return $"Clarify the claim about {matchedLower} with specific scope and current supporting evidence.";
        }

        return "This environmental claim should be clear, accurate and supported by current evidence.";
    }

    private static int CalculateComplianceScore(List<RuleFinding> ruleFindings, LlmResult llmOutput)
    {
        var baseWeights = new Dictionary<string, int>
        {
            ["high"] = 12,
            ["medium"] = 7,
            ["low"] = 4
        };

        var totalDeduction = 0;

        // deducts per category instead of per hit, so repeated pattern matches don't over-penalise
        var groupedByCategory = ruleFindings
            .GroupBy(f => (f.Category ?? "uncategorized").Trim().ToLowerInvariant())
            .Select(g =>
            {
                var severity = "low";
                if (g.Any(f => f.Severity == "high")) severity = "high";
                else if (g.Any(f => f.Severity == "medium")) severity = "medium";

                return new
                {
                    Severity = severity,
                    HitCount = g.Count()
                };
            });

        foreach (var group in groupedByCategory)
        {
            var baseDeduction = baseWeights.GetValueOrDefault(group.Severity, 4);
            var hitBonus = Math.Min(6, Math.Max(0, group.HitCount - 1) * 2);
            totalDeduction += baseDeduction + hitBonus;
        }

        // deducts extra for AI-found issues the rules missed
        var aiRisk = llmOutput.AiRiskLevel?.ToLowerInvariant() ?? "none";
        var aiExtraIssues = Math.Max(0, llmOutput.AiIssueCount - ruleFindings.Count);
        if (aiExtraIssues > 0)
        {
            var aiWeights = new Dictionary<string, int>
            {
                ["high"] = 8,
                ["medium"] = 5,
                ["low"] = 3,
                ["none"] = 2
            };
            totalDeduction += aiExtraIssues * aiWeights.GetValueOrDefault(aiRisk, 3);
        }

        if (totalDeduction == 0 && ruleFindings.Count == 0)
            return 100;

        // keeps bad scores low but stops it from always hitting 0
        return Math.Max(5, 100 - totalDeduction);
    }

}
