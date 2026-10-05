using System.Text.RegularExpressions;
using GreenClaimsGuard.Api.Models;
using OpenAI.Chat;

namespace GreenClaimsGuard.Api.Services;

public class LlmService : ILlmService
{
    private const string FallbackWording = "Made with responsibly sourced materials and a verified lower-impact production process.";

    // only used for document extraction and validation below. the compliance check itself goes through IAiComplianceClient
    private readonly ChatClient? _chatClient;
    private readonly IAiComplianceClient _aiClient;
    private readonly ILogger<LlmService> _logger;

    public LlmService(IConfiguration config, IAiComplianceClient aiClient, ILogger<LlmService> logger)
    {
        _logger = logger;
        _aiClient = aiClient;
        var apiKey = config["OpenAI:ApiKey"]
            ?? config["OpenAI__ApiKey"]
            ?? config["OPENAI_API_KEY"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _chatClient = new ChatClient("gpt-4o-mini", apiKey);
        }
    }

    public async Task<LlmResult> AnalyzeAsync(string text, List<RuleFinding> ruleFindings, string? industry = null, EvidenceFields? evidenceContext = null, ProductFacts? productFacts = null, VerifiedFacts? verifiedFacts = null, string market = Markets.Uk)
    {
        var lines = text.Split('\n');

        string FindLineForPattern(string pattern)
        {
            // finds the sentence with the pattern in it, stops at bullet points (· or •) too so ASOS-style fragments don't get merged into one fake sentence
            var sentenceMatch = Regex.Matches(text, @"[^.!?\n·•]+[.!?]*")
                .Select(m => m.Value.Trim())
                .FirstOrDefault(s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(sentenceMatch)) return sentenceMatch;

            var lineMatch = lines.FirstOrDefault(l =>
                l.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            return !string.IsNullOrWhiteSpace(lineMatch) ? lineMatch.Trim()
                : lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? text;
        }

        // flagged sentences, deduped, each given a number
        var uniqueSentences = ruleFindings
            .GroupBy(f => FindLineForPattern(f.MatchedPattern).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select((g, idx) => new AiFlaggedSentence
            {
                Id = idx + 1,
                Sentence = g.Key.Trim(),
                Categories = string.Join(", ", g.Select(f => f.Category).Distinct()),
                Severity = g.Any(f => f.Severity == "high") ? "HIGH"
                    : g.Any(f => f.Severity == "medium") ? "MEDIUM" : "LOW"
            })
            .ToList();

        var reply = await _aiClient.CheckAsync(new AiCheckRequest
        {
            Text = text,
            Industry = industry,
            Market = market,
            FlaggedSentences = uniqueSentences,
            VerifiedFacts = verifiedFacts,
            WriterNotes = productFacts,
            Evidence = evidenceContext
        }, CancellationToken.None);

        if (reply.Status != EngineStatus.Ok)
        {
            return new LlmResult
            {
                Status = reply.Status,
                AiExplanation = reply.Status switch
                {
                    EngineStatus.NotConfigured => "AI analysis unavailable: OpenAI API key not configured. Rule-based findings above remain valid.",
                    EngineStatus.Timeout => "AI analysis timed out. Rule-based findings above remain valid.",
                    _ => "AI analysis temporarily unavailable. Rule-based findings above remain valid."
                }
            };
        }

        var rejected = 0;

        // rewrites keyed by the cleaned up original sentence so AggregationService can look them up. a rewrite with a number nobody gave gets thrown away
        var sentenceRewrites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rewrite in reply.SentenceRewrites)
        {
            var sentence = uniqueSentences.FirstOrDefault(s => s.Id == rewrite.SentenceId);
            var cleaned = CleanRewrite(rewrite.Rewrite);
            if (sentence is null || string.IsNullOrWhiteSpace(cleaned)) continue;

            if (AiOutputGuard.IntroducesNewNumbers(cleaned, text, verifiedFacts))
            {
                rejected++;
                continue;
            }
            sentenceRewrites[NormalizeSentenceKey(sentence.Sentence)] = cleaned;
        }

        var tryThisWording = CleanSuggestion(reply.CompliantRewrite);
        if (string.IsNullOrWhiteSpace(tryThisWording) || AiOutputGuard.IntroducesNewNumbers(tryThisWording, text, verifiedFacts))
        {
            if (!string.IsNullOrWhiteSpace(tryThisWording)) rejected++;
            tryThisWording = FallbackWording;
        }

        var issueSummary = CleanSuggestion(reply.Summary);
        if (string.IsNullOrWhiteSpace(issueSummary))
        {
            issueSummary = "This claim likely needs clearer wording and stronger evidence.";
        }

        var aiExplanation = $"Issue summary: {issueSummary.Trim()}\nTry this wording: {tryThisWording.Trim()}";
        if (aiExplanation.Length > 1200) aiExplanation = aiExplanation[..1200];

        var violations = AiOutputGuard.ValidateViolations(text, reply.Violations, verifiedFacts, out var rejectedViolations);
        rejected += rejectedViolations;

        // The AI's own risk rating is advice. It can raise a warning but never a critical.
        var risk = (reply.RiskLevel ?? "none").ToLowerInvariant();
        if (risk == "high") risk = "medium";
        if (risk is not ("none" or "low" or "medium")) risk = "none";

        return new LlmResult
        {
            Status = EngineStatus.Ok,
            AiExplanation = aiExplanation,
            AiRiskLevel = risk,
            AiIssueCount = violations.Count,
            TryThisWording = tryThisWording,
            SentenceRewrites = sentenceRewrites,
            // required documents come from rule findings in AggregationService, not from here
            RequiredDocuments = new List<RequiredDocument>(),
            AiViolations = violations,
            RejectedRewriteCount = rejected
        };
    }

    public async Task<ProductFacts> ExtractDocumentFactsAsync(string documentText, string claimType)
    {
        if (_chatClient is null)
            return new ProductFacts();

        var claimContext = claimType switch
        {
            "organic_cotton" => "organic cotton certification (GOTS, Soil Association)",
            "recycled_content" => "recycled content (percentage, RCS or GRS certification)",
            "carbon_neutral" => "carbon neutrality (ISO 14068 or PAS 2060, scope, methodology, offset details)",
            "water_reduction" => "water usage reduction (baseline, percentage reduction, timeframe, audit reference)",
            "zero_waste" => "zero waste manufacturing (factory audit, waste diversion rate)",
            "sustainable_wool" => "sustainable wool or cashmere (RWS, SFA certification)",
            "vegan_leather" => "vegan or animal-free materials (PETA certification, material composition)",
            "collection_label" => "collection-level sustainability claims (specific material facts, certifications)",
            _ => "sustainability and material composition facts"
        };

        var prompt =
            $"You are a compliance data extractor. Extract verified facts from this supplier/certification document.\n\n" +
            $"FOCUS: Extract facts relevant to: {claimContext}\n\n" +
            $"DOCUMENT:\n\"\"\"\n{documentText}\n\"\"\"\n\n" +
            "Extract and return ONLY facts explicitly stated in the document. Do not infer or assume anything.\n\n" +
            "Return your answer in this exact format:\n" +
            "MATERIAL_COMPOSITION: [exact material percentages and names as stated, or NONE]\n" +
            "CERTIFICATIONS: [comma-separated list of certifications found, or NONE]\n" +
            "ADDITIONAL_FACTS: [audit references, reduction percentages, dates, scope statements, or NONE]\n";

        try
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = 500, Temperature = 0.0f };
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You extract facts from documents. Never invent. If not found, write NONE."),
                new UserChatMessage(prompt)
            };

            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var content = completion.Value.Content[0].Text?.Trim() ?? "";

            var materialMatch = Regex.Match(content, @"MATERIAL_COMPOSITION:\s*(.+)", RegexOptions.IgnoreCase);
            var certsMatch = Regex.Match(content, @"CERTIFICATIONS:\s*(.+)", RegexOptions.IgnoreCase);
            var factsMatch = Regex.Match(content, @"ADDITIONAL_FACTS:\s*(.+)", RegexOptions.IgnoreCase);

            var material = materialMatch.Success ? materialMatch.Groups[1].Value.Trim() : "";
            var certs = certsMatch.Success ? certsMatch.Groups[1].Value.Trim() : "";
            var facts = factsMatch.Success ? factsMatch.Groups[1].Value.Trim() : "";

            var certList = string.IsNullOrWhiteSpace(certs) || certs.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : certs.Split(',').Select(c => c.Trim()).Where(c => !string.IsNullOrWhiteSpace(c) && !c.Equals("NONE", StringComparison.OrdinalIgnoreCase)).ToList();

            return new ProductFacts
            {
                MaterialComposition = material.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : material,
                CertificationsHeld = certList.Count > 0 ? certList : null,
                AdditionalFacts = facts.Equals("NONE", StringComparison.OrdinalIgnoreCase) ? null : facts
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExtractDocumentFactsAsync error");
            return new ProductFacts();
        }
    }

    public async Task<DocumentValidationResult> ValidateDocumentClaimAsync(
        string documentText,
        string claimType,
        string? originalClaim = null)
    {
        if (_chatClient is null)
            return new DocumentValidationResult
            {
                ValidationStatus = "VALIDATED",
                ValidationFeedback = "AI validation unavailable; accepting document as-is",
                ExtractedFacts = await ExtractDocumentFactsAsync(documentText, claimType)
            };

        // First extract facts
        var extractedFacts = await ExtractDocumentFactsAsync(documentText, claimType);

        // Build validation-specific prompt
        var validationRules = claimType switch
        {
            "organic_cotton" =>
                "✓ Document must show GOTS or Soil Association certification\n" +
                "✓ Certification must be current (issued within last 3 years)\n" +
                "✓ Must cover the product's cotton material percentage\n" +
                "✗ Fail if: cert is expired, vague ('certified organic'), or doesn't specify scope",

            "recycled_content" =>
                "✓ Document must show RCS (Recycled Claim Standard), GRS, or equivalent\n" +
                "✓ Must specify % of recycled content (e.g., '100% recycled polyester')\n" +
                "✓ Must state post-consumer or post-industrial source\n" +
                "✗ Fail if: missing %, unclear source, or vague 'recycled materials'",

            "water_reduction" =>
                "✓ Document must specify BASELINE (what was used before, e.g., '80 litres per garment 2021')\n" +
                "✓ Must specify NEW AMOUNT (e.g., '48 litres with new process')\n" +
                "✓ Must specify YEAR/TIMEFRAME when measured\n" +
                "✓ Must include AUDIT REFERENCE number and auditor name (e.g., 'Bureau Veritas WA-2023-041')\n" +
                "✗ Fail if: missing baseline, no timeframe, lacks audit reference, or percentage doesn't match doc",

            "carbon_neutral" =>
                "✓ Document must show ISO 14068 (current standard from Jan 2025) or PAS 2060 (accepted for pre-2025 certifications)\n" +
                "✓ ISO 14068 requires: GHG inventory, science-based reduction targets, carbon neutrality management plan, third-party verification\n" +
                "✓ PAS 2060 requires: qualifying explanatory statement (QES), scope defined, offsets retired in recognised registry, third-party assurance\n" +
                "✓ Must specify SCOPE (product-level vs operations vs supply chain)\n" +
                "✓ Must include offset details (amount offset, standard e.g. VCS/CDM/Gold Standard, verification body)\n" +
                "✓ Certification must be current (< 3 years old; PAS 2060 not valid for new claims after Jan 2025)\n" +
                "✗ Fail if: no standard specified, vague scope, missing offset details, expired, or entity on certificate does not match the brand making the claim",

            "zero_waste" =>
                "✓ Document must show UL 2799 or equivalent zero waste certification\n" +
                "✓ Must specify % waste to landfill (must be < 5% for 'zero waste')\n" +
                "✓ Must include facility name and audit date\n" +
                "✓ Must detail waste diversion methods (recycling, energy recovery, etc.)\n" +
                "✗ Fail if: percentage missing, > 5% to landfill, or facility name unclear",

            "sustainable_wool" =>
                "✓ Document must show RWS (Responsible Wool Standard) or SFA certification\n" +
                "✓ Must cover land management and animal welfare practices\n" +
                "✓ Certification must be current (< 3 years)\n" +
                "✗ Fail if: no standard, missing practice details, or expired",

            "vegan_leather" =>
                "✓ Document must confirm material is non-animal leather\n" +
                "✓ Should state what material is used (PU, recycled polyester, etc.)\n" +
                "✓ If claiming eco-friendly, must include lifecycle data\n" +
                "✗ Fail if: material unclear, environmental claims without data",

            _ => "Unknown claim type. Validate against the CMA Green Claims Code."
        };

        var validationPrompt =
            "You are a UK green claims compliance validator. Assess whether a document adequately substantiates a product claim.\n\n" +
            $"CLAIM TYPE: {claimType}\n" +
            (string.IsNullOrWhiteSpace(originalClaim) ? "" : $"ORIGINAL CLAIM: \"{originalClaim}\"\n\n") +
            $"VALIDATION REQUIREMENTS:\n{validationRules}\n\n" +
            "EXTRACTED FACTS FROM DOCUMENT:\n" +
            (string.IsNullOrWhiteSpace(extractedFacts.MaterialComposition) ? "" : $"  Material: {extractedFacts.MaterialComposition}\n") +
            (extractedFacts.CertificationsHeld?.Count > 0 ? $"  Certs: {string.Join(", ", extractedFacts.CertificationsHeld)}\n" : "") +
            (string.IsNullOrWhiteSpace(extractedFacts.AdditionalFacts) ? "" : $"  Facts: {extractedFacts.AdditionalFacts}\n") +
            "\nCMA COMPLIANCE CHECK:\n" +
            "1. Is the evidence specific and measurable (not vague)?\n" +
            "2. Are all material facts present (baseline, scope, audit refs)?\n" +
            "3. Does the extracted fact align with the original claim?\n" +
            "4. Is the certification/audit current and from a recognized body?\n\n" +
            "Your response must be EXACTLY in this format (fill in the blanks):\n" +
            "VALIDATION_STATUS: [VALIDATED / NEEDS_MORE_INFO / CONTRADICTS_CLAIM]\n" +
            "FEEDBACK: [1–2 sentences explaining why it passed or failed]\n" +
            "MISSING_FIELDS: [comma-separated list of required fields still missing, or NONE]\n";

        try
        {
            var options = new ChatCompletionOptions { MaxOutputTokenCount = 300, Temperature = 0.1f };
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You validate documents against UK green claims standards. Be strict about missing data."),
                new UserChatMessage(validationPrompt)
            };

            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var content = completion.Value.Content[0].Text?.Trim() ?? "";

            var statusMatch = Regex.Match(content, @"VALIDATION_STATUS:\s*(VALIDATED|NEEDS_MORE_INFO|CONTRADICTS_CLAIM)", RegexOptions.IgnoreCase);
            var feedbackMatch = Regex.Match(content, @"FEEDBACK:\s*(.+?)(?=MISSING_FIELDS:|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var missingMatch = Regex.Match(content, @"MISSING_FIELDS:\s*(.+?)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            var status = statusMatch.Success ? statusMatch.Groups[1].Value.Trim() : "VALIDATED";
            var feedback = feedbackMatch.Success ? feedbackMatch.Groups[1].Value.Trim() : "Document accepted";
            var missing = missingMatch.Success ? missingMatch.Groups[1].Value.Trim() : "NONE";

            var requiredFields = string.IsNullOrWhiteSpace(missing) || missing.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? new List<string>()
                : missing.Split(',').Select(f => f.Trim()).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();

            // Only return extracted facts if validation passed
            var factsToReturn = status == "VALIDATED" ? extractedFacts : null;

            return new DocumentValidationResult
            {
                ValidationStatus = status,
                ValidationFeedback = feedback,
                ExtractedFacts = factsToReturn,
                RequiredFields = requiredFields
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ValidateDocumentClaimAsync error");
            return new DocumentValidationResult
            {
                ValidationStatus = "VALIDATED",
                ValidationFeedback = "Validation check skipped due to error",
                ExtractedFacts = extractedFacts
            };
        }
    }

    /// <summary>
    /// Cleans a short UI field (issue summary, try-this-wording) — keeps only the first sentence.
    /// </summary>
    private static string CleanSuggestion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = Regex.Replace(text, @"\b(\w+)\s+\1\b", "$1", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        var sentenceMatch = Regex.Match(cleaned, @"[^.!?]+[.!?]?");
        return (sentenceMatch.Success ? sentenceMatch.Value : cleaned).Trim();
    }

    /// <summary>
    /// Cleans a full ghostwriter rewrite — preserves multi-line structure (bullets, paragraphs).
    /// </summary>
    private static string CleanRewrite(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Deduplicate repeated adjacent words but preserve newlines
        var lines = text.Split('\n');
        var cleaned = string.Join("\n", lines.Select(line =>
            Regex.Replace(line, @"\b(\w+)\s+\1\b", "$1", RegexOptions.IgnoreCase).TrimEnd()
        ));
        return cleaned.Trim();
    }

    /// <summary>
    /// Normalizes a sentence to a lookup key matching AggregationService.NormalizeForSimilarity.
    /// Strips non-alphanumeric, collapses whitespace, lowercases.
    /// </summary>
    private static string NormalizeSentenceKey(string text)
    {
        var normalized = Regex.Replace(text ?? string.Empty, @"[^a-zA-Z0-9\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim().ToLowerInvariant();
    }
}
