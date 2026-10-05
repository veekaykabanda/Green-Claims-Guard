namespace GreenClaimsGuard.Api.Models;

public static class ComplianceStatus
{
    public const string ChangesRequired = "CHANGES_REQUIRED";
    public const string ReadyToPublishSubjectToReview = "READY_TO_PUBLISH_SUBJECT_TO_REVIEW";
    public const string Published = "PUBLISHED";
    // writer pulled it back before anyone signed off, it's a draft again
    public const string Withdrawn = "WITHDRAWN";
    // editor sent it back with a reason, writer edits and resubmits
    public const string SentBack = "SENT_BACK";
}

public static class Markets
{
    public const string Uk = "UK";
    public const string Eu = "EU";

    // no market given defaults to UK, anything that's not UK or EU gets rejected
    public static bool TryNormalise(string? value, out string market)
    {
        var trimmed = (value ?? string.Empty).Trim();
        market = trimmed.Length == 0 ? Uk : trimmed.ToUpperInvariant();
        return market is Uk or Eu;
    }
}

// status for one engine on one check, so the UI and audit log know what ran
public static class EngineStatus
{
    public const string Ok = "Ok";
    public const string Skipped = "Skipped";           // deliberately not run (e.g. live rules-only check)
    public const string NotConfigured = "NotConfigured"; // e.g. no OpenAI key, no database
    public const string Failed = "Failed";
    public const string Unavailable = "Unavailable";   // configured but could not be reached
    public const string Timeout = "Timeout";           // did not answer within the allowed time
    public const string NotChecked = "NotChecked";     // not probed for this kind of check
}

public static class ComplianceDecision
{
    public const string AppliedSuggestion = "APPLIED_SUGGESTION";
    public const string KeptOriginalWithJustification = "KEPT_ORIGINAL_WITH_JUSTIFICATION";
}

public class EvidenceFields
{
    public string? Baseline { get; set; }
    public double? ReductionPercentage { get; set; }
    public string? Timeframe { get; set; }
    public string? EvidenceReference { get; set; }
}

public class DocumentValidationResult
{
    public string ValidationStatus { get; set; } = ""; // VALIDATED, NEEDS_MORE_INFO, CONTRADICTS_CLAIM
    public string ValidationFeedback { get; set; } = "";
    public ProductFacts? ExtractedFacts { get; set; }
    public List<string> RequiredFields { get; set; } = new();
}

public class IssueDecisionInput
{
    public string IssueId { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? UserDecision { get; set; }
    public string? UserJustification { get; set; }
    public bool RequiresEvidence { get; set; }
    public EvidenceFields? EvidenceFields { get; set; }
    public string? ResolvedSentenceSignature { get; set; }

    // server fills this in at submission from the matched issue, never comes from the client
    public string? Phrase { get; set; }
    public string? Category { get; set; }
}

public class MarkReadyRequest
{
    public Guid ProductId { get; set; }
    public string FinalDescription { get; set; } = "";
    public List<IssueDecisionInput> IssueDecisions { get; set; } = new();
    public ProductFacts? ProductFacts { get; set; }

    // UK or EU, defaults to UK, saved with the submission
    public string? Market { get; set; }

    // category, subcategory and tags get checked and saved too, so publish re-check can see them
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public string? Tags { get; set; }
}

public class MarkReadyResponse
{
    public string OverallStatus { get; set; } = ComplianceStatus.ChangesRequired;
    public string Message { get; set; } = "";
    public bool ReadyToPublishSubjectToReview { get; set; }
    public List<string> ValidationErrors { get; set; } = new();
    public string RulesStatus { get; set; } = EngineStatus.NotChecked;
    public string AiStatus { get; set; } = EngineStatus.NotChecked;
    public string DbStatus { get; set; } = EngineStatus.NotChecked;
    public string Market { get; set; } = Markets.Uk;
    public string RulesVersion { get; set; } = "";

    // writer kept a critical issue with a reason, so publishing needs editor override
    public bool NeedsOverride { get; set; }

    // true if this used the saved draft text, not what was sent in the request
    public bool UsedDraft { get; set; }
}

public class PendingReviewItem
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string FinalDescription { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
    public string? SubmittedByUserId { get; set; }
    // comes from the user directory, null until that person has signed in with an email
    public string? SubmittedByEmail { get; set; }
    public string? Market { get; set; }
    public string? AiStatus { get; set; }
    public int? OpenIssueCount { get; set; }
    public bool NeedsOverride { get; set; }

    // editor can't sign off their own work unless they're the only one, CanSignOff says if that applies
    public bool IsOwnSubmission { get; set; }
    public bool CanSignOff { get; set; }
}

public class SendBackRequest
{
    public Guid ProductId { get; set; }
    public string? ReasonCategory { get; set; }
    public string? Comment { get; set; }
}

public class WithdrawRequest
{
    public Guid ProductId { get; set; }
}

public class PublishRequest
{
    public Guid ProductId { get; set; }

    // editor can publish past a block, but only with a written reason
    public string? OverrideReason { get; set; }
}

public class PublishResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public bool Overridden { get; set; }
    // true when the only editor signed off their own submission, allowed but flagged in the audit log
    public bool SelfSignOff { get; set; }
    public List<string> BlockingReasons { get; set; } = new();
}
