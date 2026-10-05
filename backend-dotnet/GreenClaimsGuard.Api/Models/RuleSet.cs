using System.Text.Json.Serialization;

namespace GreenClaimsGuard.Api.Models;

public class RuleSet
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    // "UK" or "EU", if a file doesn't say, it's treated as UK
    [JsonPropertyName("jurisdiction")]
    public string Jurisdiction { get; set; } = Markets.Uk;

    [JsonPropertyName("rules")]
    public List<Rule> Rules { get; set; } = new();
}

public class Rule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("patterns")]
    public List<string> Patterns { get; set; } = new();

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("regulation")]
    public string Regulation { get; set; } = "";

    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = "";
}
