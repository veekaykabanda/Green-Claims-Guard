namespace GreenClaimsGuard.Api.Models;

// these reasons map to the CMA Green Claims Code so writers know which rule they broke, not just that they broke one
public static class SendBackReasons
{
    public static readonly IReadOnlyList<(string Code, string Label)> All = new[]
    {
        ("not_accurate", "Not accurate"),
        ("vague_or_unclear", "Vague or unclear"),
        ("missing_information", "Missing information"),
        ("unfair_comparison", "Unfair comparison"),
        ("not_full_life_cycle", "Not full life cycle"),
        ("not_substantiated", "Not substantiated"),
    };

    public static bool TryGetLabel(string? code, out string label)
    {
        var match = All.FirstOrDefault(r => string.Equals(r.Code, (code ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        label = match.Label ?? string.Empty;
        return match.Code is not null;
    }

    // The stored code, whatever capitalisation the caller used.
    public static string? Normalise(string? code) =>
        All.FirstOrDefault(r => string.Equals(r.Code, (code ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)).Code;

    public static string LabelFor(string? code) => TryGetLabel(code, out var label) ? label : (code ?? string.Empty);
}
