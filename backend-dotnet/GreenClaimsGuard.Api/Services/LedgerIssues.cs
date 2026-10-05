using System.Text.Json;

namespace GreenClaimsGuard.Api.Services;

public record LedgerIssue(string? Phrase, string? Category, bool Critical, bool Resolved);

// audit ledger stores each check's issues as JSON, read back here by both overviews. a row that can't be read just counts as no issues
public static class LedgerIssues
{
    public static List<LedgerIssue> Parse(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            return doc.RootElement.EnumerateArray().Select(i => new LedgerIssue(
                Text(i, "Phrase"),
                Text(i, "Category"),
                i.TryGetProperty("Severity", out var s) && string.Equals(s.GetString(), "high", StringComparison.OrdinalIgnoreCase),
                i.TryGetProperty("Resolved", out var r) && r.ValueKind == JsonValueKind.True)).ToList();
        }
        catch (JsonException)
        {
            return new List<LedgerIssue>();
        }
    }

    private static string? Text(JsonElement issue, string property) =>
        issue.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
