namespace GreenClaimsGuard.Api.Models;

public class RuleFinding
{
    public string RuleId { get; set; } = "";
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Regulation { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string MatchedPattern { get; set; } = "";
}
