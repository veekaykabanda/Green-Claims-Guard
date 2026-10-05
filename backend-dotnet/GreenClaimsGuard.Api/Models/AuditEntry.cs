namespace GreenClaimsGuard.Api.Models;

public static class AuditActions
{
    public const string Check = "Check";
    public const string Apply = "Apply";
    public const string Keep = "Keep";
    public const string Submit = "Submit";
    public const string Publish = "Publish";
    public const string Override = "Override";
    public const string Withdraw = "Withdraw";
    public const string SendBack = "SendBack";
    public const string FactsChange = "FactsChange";
    public const string DataTransfer = "DataTransfer";
}

public static class AuditOutcomes
{
    public const string Ok = "Ok";
    public const string Blocked = "Blocked";
}

// one row in the audit log, what happened, what the checks said, and the exact text, never updated after that
public class AuditEntry
{
    public long Id { get; set; }
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = AuditOutcomes.Ok;
    public string CopySnapshot { get; set; } = "";
    // hash of the snapshot, so you can tell if two versions are the same text without storing it twice
    public string CopyHash { get; set; } = "";
    public string Market { get; set; } = Markets.Uk;
    public string RulesStatus { get; set; } = EngineStatus.NotChecked;
    public string AiStatus { get; set; } = EngineStatus.NotChecked;
    public string RulesVersion { get; set; } = "";
    public string IssuesJson { get; set; } = "[]";
    public string? Justification { get; set; }
    public string? Detail { get; set; }
    public string? UserId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class AuditEntryDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid? ProductId { get; set; }
    public string? ProductName { get; set; }
    public string? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Market { get; set; } = "";
    public string RulesVersion { get; set; } = "";
    public string RulesStatus { get; set; } = "";
    public string AiStatus { get; set; } = "";
    public string? Justification { get; set; }
    public string? Detail { get; set; }
    public string CopyHash { get; set; } = "";
    public string CopySnapshot { get; set; } = "";
    public string IssuesJson { get; set; } = "[]";
}
