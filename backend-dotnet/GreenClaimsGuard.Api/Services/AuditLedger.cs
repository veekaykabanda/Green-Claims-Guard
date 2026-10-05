using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface IAuditLedger
{
    // saves entries, returns false and logs if it couldn't save. a ledger failure should never turn a blocked action into an allowed one, so it's up to the caller to decide what that means
    Task<bool> TryRecordAsync(params AuditEntry[] entries);
}

public class AuditLedger : IAuditLedger
{
    private readonly AppDbContext _db;
    private readonly IDbService _dbService;
    private readonly ILogger<AuditLedger> _logger;

    public AuditLedger(AppDbContext db, IDbService dbService, ILogger<AuditLedger> logger)
    {
        _db = db;
        _dbService = dbService;
        _logger = logger;
    }

    public async Task<bool> TryRecordAsync(params AuditEntry[] entries)
    {
        if (entries.Length == 0) return true;
        if (!_dbService.IsConfigured) return false;

        try
        {
            _db.AuditLedger.AddRange(entries);
            await _db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write {Count} audit ledger entries", entries.Length);
            foreach (var entry in entries)
            {
                _db.Entry(entry).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }
            return false;
        }
    }
}

public static class AuditEntryFactory
{
    public static string HashOf(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();

    // builds the entry from the server's own check, so the ledger records what the server saw, never what the client claimed
    public static AuditEntry From(
        string action,
        string outcome,
        string text,
        AnalyzeResponse evaluation,
        string? userId,
        Guid? productId,
        string? justification = null,
        string? detail = null)
    {
        var issues = evaluation.GroupedFindings.Select(f => new
        {
            f.IssueId,
            f.Category,
            f.Severity,
            Phrase = f.MatchedPatterns.FirstOrDefault(),
            Resolved = f.IsResolved,
            f.UserDecision
        });

        return new AuditEntry
        {
            ProductId = productId,
            Action = action,
            Outcome = outcome,
            CopySnapshot = text,
            CopyHash = HashOf(text),
            Market = evaluation.Market,
            RulesStatus = evaluation.RulesStatus,
            AiStatus = evaluation.AiStatus,
            RulesVersion = evaluation.RulesVersion,
            IssuesJson = JsonSerializer.Serialize(issues),
            Justification = justification,
            Detail = detail,
            UserId = userId,
            Timestamp = DateTime.UtcNow
        };
    }
}

// Writes the ledger as CSV a senior editor can open in a spreadsheet.
public static class AuditCsv
{
    private static readonly string[] Columns =
    {
        "Timestamp (UTC)", "Product", "Product ID", "User", "User email", "Action", "Outcome", "Market",
        "Rules version", "Rules status", "AI status", "Justification", "Detail", "Copy hash", "Copy snapshot"
    };

    public static string Build(IEnumerable<AuditEntryDto> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", Columns.Select(Cell)));

        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                r.ProductName, r.ProductId?.ToString(), r.UserId, r.UserEmail, r.Action, r.Outcome, r.Market,
                r.RulesVersion, r.RulesStatus, r.AiStatus, r.Justification, r.Detail, r.CopyHash, r.CopySnapshot
            }.Select(Cell)));
        }

        return csv.ToString();
    }

    // quotes every cell and defuses anything a spreadsheet would treat as a formula, since justification and copy text can't be trusted not to start with = + - @
    private static string Cell(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0]))
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
