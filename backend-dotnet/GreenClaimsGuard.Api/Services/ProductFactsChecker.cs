using System.Globalization;
using System.Text.RegularExpressions;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public enum FactMismatchKind
{
    Percentage,
    MaterialNotListed,
    QualifierNotListed,
    PureClaim,
    Certification,
    Origin
}

public class FactMismatch
{
    public FactMismatchKind Kind { get; init; }

    // The exact words in the copy that make the claim, so the UI can highlight them.
    public string Phrase { get; init; } = "";

    public string Message { get; init; } = "";

    // The whole sentence rewritten so it matches the verified facts. Safe to apply as-is.
    public string CorrectedSentence { get; init; } = "";
}

public class FactsCheckResult
{
    public List<FactMismatch> Mismatches { get; } = new();

    // counts claims about composition, certification or origin, even if there were no facts to check them against
    public int CheckableClaimCount { get; set; }
}

// pulls claims out of the copy and compares them to the verified facts. no AI involved, so with no facts on file it just counts the claims instead
public static class ProductFactsChecker
{
    private const decimal PercentageTolerance = 0.5m;

    private static readonly Dictionary<string, string> MaterialFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cotton"] = "cotton",
        ["polyester"] = "polyester",
        ["linen"] = "linen", ["flax"] = "linen",
        ["wool"] = "wool", ["merino"] = "wool", ["lambswool"] = "wool",
        ["viscose"] = "viscose", ["rayon"] = "viscose",
        ["lyocell"] = "lyocell", ["tencel"] = "lyocell",
        ["nylon"] = "nylon", ["polyamide"] = "nylon",
        ["elastane"] = "elastane", ["spandex"] = "elastane", ["lycra"] = "elastane",
        ["acrylic"] = "acrylic",
        ["silk"] = "silk",
        ["cashmere"] = "cashmere",
        ["hemp"] = "hemp",
        ["modal"] = "modal",
        ["leather"] = "leather"
    };

    private static readonly string MaterialWords =
        string.Join("|", MaterialFamilies.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));

    // "60% organic cotton", "100 per cent recycled polyester", "40% polyester"
    private static readonly Regex PercentageClaim = new(
        @"(?<pct>\d{1,3}(?:[.,]\d+)?)\s*(?:%|per\s?cent)\s*(?<mods>(?:[A-Za-z][A-Za-z-]*\s+){0,3}?)(?<mat>" + MaterialWords + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "pure cotton", "entirely wool", "all linen"
    private static readonly Regex PureClaim = new(
        @"\b(?<abs>pure|entirely|solely|exclusively|all)\s+(?<mods>(?:[A-Za-z][A-Za-z-]*\s+){0,2}?)(?<mat>" + MaterialWords + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CertificationMention = new(
        @"\b(?<cert>GOTS|GRS|RCS|OCS|OEKO-?TEX(?:\s?(?:Standard\s?)?100)?|Fair\s?trade|FSC|bluesign|RWS|RDS|BCI|Better\s?Cotton|Cradle\s?to\s?Cradle)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CertificationContext = new(
        @"certif|accredit|approved|standard|verified|label|licen[cs]ed",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The place must start with a capital, so "made in the round" is not a claim about a country.
    private static readonly Regex OriginClaim = new(
        @"\b(?i:made|manufactured|produced|crafted|woven|knitted|sewn)\s+in\s+(?:(?i:the)\s+)?(?<place>[A-Z][a-zA-Z]+(?:\s[A-Z][a-zA-Z]+)?)",
        RegexOptions.Compiled);

    // Words that turn "40% ... polyester" into a reduction claim rather than a composition claim.
    private static readonly HashSet<string> NotACompositionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "reduction", "reduce", "reduced", "less", "lower", "fewer", "saving", "savings", "cut",
        "emissions", "water", "energy", "carbon", "waste", "more", "increase", "increased"
    };

    private static readonly string[] Qualifiers = { "organic", "recycled" };

    private static readonly Dictionary<string, string> PlaceSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = "uk", ["u.k."] = "uk", ["united kingdom"] = "uk", ["britain"] = "uk", ["great britain"] = "uk",
        ["england"] = "uk", ["scotland"] = "uk", ["wales"] = "uk", ["northern ireland"] = "uk",
        ["turkey"] = "turkey", ["turkiye"] = "turkey", ["türkiye"] = "turkey",
        ["usa"] = "usa", ["united states"] = "usa", ["america"] = "usa"
    };

    public static FactsCheckResult Check(string text, VerifiedFacts? facts)
    {
        var result = new FactsCheckResult();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var haveFacts = facts is { IsEmpty: false };
        var seenPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(FactMismatchKind kind, string phrase, string message, string replacementPhrase)
        {
            if (!seenPhrases.Add(phrase)) return;
            result.Mismatches.Add(new FactMismatch
            {
                Kind = kind,
                Phrase = phrase,
                Message = message,
                CorrectedSentence = CorrectedSentence(text, phrase, replacementPhrase)
            });
        }

        foreach (Match m in PercentageClaim.Matches(text))
        {
            var mods = m.Groups["mods"].Value;
            if (mods.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => NotACompositionWords.Contains(w))) continue;
            if (!TryParsePercentage(m.Groups["pct"].Value, out var claimed)) continue;

            result.CheckableClaimCount++;
            if (!haveFacts) continue;

            var family = MaterialFamilies[m.Groups["mat"].Value];
            var qualifier = Qualifiers.FirstOrDefault(q => mods.Contains(q, StringComparison.OrdinalIgnoreCase));
            var material = m.Groups["mat"].Value;
            var phrase = m.Value.Trim();

            var ofFamily = facts!.Materials.Where(x => FamilyOf(x.Material) == family).ToList();
            if (ofFamily.Count == 0)
            {
                Add(FactMismatchKind.MaterialNotListed, phrase,
                    $"The copy claims {phrase}, but the verified facts list no {family}.", string.Empty);
                continue;
            }

            var comparable = ofFamily;
            if (qualifier is not null)
            {
                comparable = ofFamily.Where(x => x.Material.Contains(qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
                if (comparable.Count == 0)
                {
                    // fixes the percentage too, not just the qualifier, since what's verified is just the plain figure
                    var listed = ofFamily.Sum(x => x.Percentage).ToString("0.##", CultureInfo.InvariantCulture);
                    var without = $"{listed}% {RemoveWord(mods, qualifier)}{material}".Replace("  ", " ").Trim();
                    Add(FactMismatchKind.QualifierNotListed, phrase,
                        $"The copy claims {phrase}, but the verified facts list {listed}% {family} and none of it is \"{qualifier}\".", without);
                    continue;
                }
            }

            var verified = comparable.Sum(x => x.Percentage);
            if (Math.Abs(verified - claimed) > PercentageTolerance)
            {
                var corrected = $"{verified.ToString("0.##", CultureInfo.InvariantCulture)}% {mods}{material}".Replace("  ", " ").Trim();
                Add(FactMismatchKind.Percentage, phrase,
                    $"The copy claims {claimed.ToString("0.##", CultureInfo.InvariantCulture)}% but the verified facts say {verified.ToString("0.##", CultureInfo.InvariantCulture)}% for {(qualifier is null ? family : qualifier + " " + family)}.",
                    corrected);
            }
        }

        foreach (Match m in PureClaim.Matches(text))
        {
            result.CheckableClaimCount++;
            if (!haveFacts) continue;

            var family = MaterialFamilies[m.Groups["mat"].Value];
            var others = facts!.Materials
                .Where(x => x.Percentage > 0 && FamilyOf(x.Material) != family)
                .Select(x => FamilyOf(x.Material) ?? x.Material)
                .Distinct()
                .ToList();
            var claimsListed = facts.Materials.Any(x => FamilyOf(x.Material) == family);

            if (others.Count > 0 || !claimsListed)
            {
                var phrase = m.Value.Trim();
                Add(FactMismatchKind.PureClaim, phrase,
                    others.Count > 0
                        ? $"The copy claims \"{phrase}\", but the verified composition also includes {string.Join(", ", others)}."
                        : $"The copy claims \"{phrase}\", but the verified facts list no {family}.",
                    m.Groups["mods"].Value + m.Groups["mat"].Value);
            }
        }

        foreach (Match m in CertificationMention.Matches(text))
        {
            var from = Math.Max(0, m.Index - 40);
            var to = Math.Min(text.Length, m.Index + m.Length + 40);
            if (!CertificationContext.IsMatch(text.Substring(from, to - from))) continue;

            result.CheckableClaimCount++;
            if (!haveFacts) continue;

            var cert = NormalizeCertification(m.Groups["cert"].Value);
            if (!facts!.Certifications.Any(c => NormalizeCertification(c) == cert))
            {
                var phrase = CertificationPhrase(text, m);
                Add(FactMismatchKind.Certification, phrase,
                    $"The copy claims {m.Groups["cert"].Value.Trim()} certification, which is not in the verified facts.", string.Empty);
            }
        }

        foreach (Match m in OriginClaim.Matches(text))
        {
            result.CheckableClaimCount++;
            if (!haveFacts) continue;

            var place = m.Groups["place"].Value.Trim();
            if (!OriginSupported(place, facts!.Origin))
            {
                var phrase = m.Value.Trim();
                Add(FactMismatchKind.Origin, phrase,
                    string.IsNullOrWhiteSpace(facts.Origin)
                        ? $"The copy claims \"{phrase}\", but no origin is recorded in the verified facts."
                        : $"The copy claims \"{phrase}\", but the verified origin is {facts.Origin}.",
                    string.IsNullOrWhiteSpace(facts.Origin) ? string.Empty : $"made in {facts.Origin}");
            }
        }

        return result;
    }

    private static string? FamilyOf(string material)
    {
        foreach (var word in Regex.Matches(material ?? string.Empty, @"[A-Za-z]+").Select(m => m.Value))
        {
            if (MaterialFamilies.TryGetValue(word, out var family)) return family;
        }
        return null;
    }

    private static bool TryParsePercentage(string raw, out decimal value) =>
        decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static string RemoveWord(string words, string word) =>
        Regex.Replace(words, @"\b" + Regex.Escape(word) + @"\b\s*", string.Empty, RegexOptions.IgnoreCase);

    private static string NormalizeCertification(string name)
    {
        var key = Regex.Replace(name ?? string.Empty, @"[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
        if (key.StartsWith("OEKOTEX")) return "OEKOTEX";
        if (key is "BETTERCOTTON" or "BCI") return "BCI";
        if (key is "FAIRTRADE") return "FAIRTRADE";
        return key;
    }

    private static string CanonicalPlace(string place)
    {
        var lower = place.Trim().ToLowerInvariant();
        return PlaceSynonyms.TryGetValue(lower, out var canonical) ? canonical : lower;
    }

    private static bool OriginSupported(string claimedPlace, string? factsOrigin)
    {
        if (string.IsNullOrWhiteSpace(factsOrigin)) return false;

        var claimed = CanonicalPlace(claimedPlace);
        var origin = factsOrigin.ToLowerInvariant();

        if (origin.Contains(claimed, StringComparison.Ordinal)) return true;

        foreach (var (synonym, canonical) in PlaceSynonyms)
        {
            if (canonical == claimed && Regex.IsMatch(origin, @"(?<![a-z])" + Regex.Escape(synonym) + @"(?![a-z])"))
                return true;
        }

        return false;
    }

    // "GOTS-certified" or "certified by GOTS", the words that make it a claim move with the name
    private static string CertificationPhrase(string text, Match m)
    {
        var start = m.Index;
        var end = m.Index + m.Length;

        var before = Regex.Match(text.Substring(0, start), @"(?i)certified\s+(?:by|to|under)\s+$");
        if (before.Success) start = before.Index;

        var after = Regex.Match(text.Substring(end), @"^(?i)[\s-]*(?:certified|certification|accredited|approved|standard|verified)\b");
        if (after.Success) end += after.Length;

        return text.Substring(start, end - start).Trim();
    }

    private static string CorrectedSentence(string text, string phrase, string replacementPhrase)
    {
        // stops at bullet points (· or •) too so ASOS-style fragments don't get merged into one fake sentence
        var sentence = Regex.Matches(text, @"[^.!?\n·•]+[.!?]*")
            .Select(m => m.Value.Trim())
            .FirstOrDefault(s => s.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            ?? phrase;

        var index = sentence.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        var rewritten = index < 0
            ? sentence
            : sentence.Remove(index, phrase.Length).Insert(index, replacementPhrase.Trim());

        rewritten = Regex.Replace(rewritten, @"\s{2,}", " ");
        rewritten = Regex.Replace(rewritten, @"\s+([,.;:!?])", "$1");
        rewritten = Regex.Replace(rewritten, @"\b(?:from|with|in|of|and)\s*([,.;:!?]|$)", "$1", RegexOptions.IgnoreCase);
        rewritten = Regex.Replace(rewritten, @"^[\s,-]+", string.Empty);
        return rewritten.Trim();
    }
}
