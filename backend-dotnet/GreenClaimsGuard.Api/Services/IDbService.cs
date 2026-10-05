using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface IDbService
{
    bool IsConfigured { get; }

    // True only when a database is configured and reachable right now.
    Task<bool> IsAvailableAsync();
    Task LogAnalysisAsync(string text, AnalyzeResponse result, List<RuleFinding> ruleFindings);
    Task<DbStatus> CheckStatusAsync();
}

public class DbStatus
{
    public bool Configured { get; set; }
    public bool Connected { get; set; }
    public bool TableExists { get; set; }
    public string Message { get; set; } = "";
}
