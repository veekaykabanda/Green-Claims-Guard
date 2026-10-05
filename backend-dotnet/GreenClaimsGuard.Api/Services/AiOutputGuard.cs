using System.Text.RegularExpressions;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// treats AI output as advice from an untrusted source, checks it against the copy and verified facts before it reaches a user
public static class AiOutputGuard
{
    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    // true if the rewrite has a number that isn't in the original copy or the verified facts, like a made up "80% recycled", that's exactly what this stops
    public static bool IntroducesNewNumbers(string rewrite, string originalText, VerifiedFacts? facts)
    {
        var allowed = NumbersIn(originalText);
        if (facts is not null)
        {
            foreach (var m in facts.Materials)
            {
                allowed.Add(Normalise(m.Percentage.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
                allowed.UnionWith(NumbersIn(m.Material));
            }
            foreach (var c in facts.Certifications) allowed.UnionWith(NumbersIn(c));
            allowed.UnionWith(NumbersIn(facts.Origin ?? ""));
        }

        return NumbersIn(rewrite).Any(n => !allowed.Contains(n));
    }

    // only keeps violations where the phrase is actually in the text and has a real replacement. offsets from the model aren't trusted, they get recalculated from the text
    public static List<AiViolation> ValidateViolations(string text, IEnumerable<AiCheckViolation> violations, VerifiedFacts? facts, out int rejected)
    {
        rejected = 0;
        var kept = new List<AiViolation>();
        foreach (var v in violations)
        {
            var phrase = (v.Phrase ?? "").Trim();
            var replacement = (v.Replacement ?? "").Trim();
            var start = phrase.Length == 0 ? -1 : text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);

            if (start < 0 || replacement.Length == 0 || string.IsNullOrWhiteSpace(v.RuleViolated))
            {
                rejected++;
                continue;
            }
            if (IntroducesNewNumbers(replacement, text, facts))
            {
                rejected++;
                continue;
            }
            if (kept.Any(k => k.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase))) continue;

            kept.Add(new AiViolation
            {
                Phrase = text.Substring(start, phrase.Length),
                Rule = v.RuleViolated.Trim(),
                Replacement = replacement
            });
        }
        return kept;
    }

    private static HashSet<string> NumbersIn(string value) =>
        Number.Matches(value ?? "").Select(m => Normalise(m.Value)).ToHashSet();

    private static string Normalise(string number)
    {
        var n = number.Replace(',', '.');
        if (n.Contains('.')) n = n.TrimEnd('0').TrimEnd('.');
        return n;
    }
}
