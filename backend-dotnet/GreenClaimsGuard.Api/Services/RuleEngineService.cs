using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public class RuleEngineService : IRuleEngineService
{
    private static readonly string[] RuleFiles = { "cma_rules.json", "asa_rules.json", "fashion_rules.json", "ecgt_rules.json" };

    private readonly List<RuleSet> _ruleSets;
    private readonly Dictionary<string, string> _versions = new();
    private readonly ILogger<RuleEngineService> _logger;

    public RuleEngineService(ILogger<RuleEngineService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _ruleSets = new List<RuleSet>();

        var rulesDir = Path.Combine(env.ContentRootPath, "Rules");
        var versionInputs = new Dictionary<string, StringBuilder>
        {
            [Markets.Uk] = new StringBuilder(),
            [Markets.Eu] = new StringBuilder()
        };

        foreach (var file in RuleFiles)
        {
            var path = Path.Combine(rulesDir, file);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var ruleSet = JsonSerializer.Deserialize<RuleSet>(json);
                if (ruleSet != null)
                {
                    ruleSet.Jurisdiction = ruleSet.Jurisdiction.Trim().ToUpperInvariant();
                    if (!versionInputs.TryGetValue(ruleSet.Jurisdiction, out var input))
                    {
                        _logger.LogWarning("Rule file {File} has unknown jurisdiction {Jurisdiction}; skipped", file, ruleSet.Jurisdiction);
                        continue;
                    }

                    _ruleSets.Add(ruleSet);
                    input.Append(file).Append('\n').Append(json).Append('\n');
                    _logger.LogInformation("Loaded {Count} {Jurisdiction} rules from {File}", ruleSet.Rules.Count, ruleSet.Jurisdiction, file);
                }
            }
            else
            {
                _logger.LogWarning("Rule file not found: {Path}", path);
            }
        }

        // one version per market, just a hash of that market's rule files so it only changes when the rules do
        foreach (var (market, input) in versionInputs)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString())));
            _versions[market] = $"{market}-{hash[..12].ToLowerInvariant()}";
        }
    }

    public string RulesVersionFor(string market) =>
        _versions.TryGetValue(market, out var version) ? version : _versions[Markets.Uk];

    public IReadOnlyList<RuleSet> RuleSetsFor(string market) =>
        _ruleSets.Where(r => r.Jurisdiction == market).ToList();

    public int RuleCountFor(string market) =>
        _ruleSets.Where(r => r.Jurisdiction == market).Sum(r => r.Rules.Count);

    public List<RuleFinding> Analyze(string text, string market = Markets.Uk)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<RuleFinding>();
        var normalizedText = text.ToLowerInvariant();
        var findings = new List<RuleFinding>();
        var matchedPositions = new HashSet<int>();

        // Collect all potential matches with position info
        var potentialMatches = new List<PotentialMatch>();

        foreach (var ruleSet in _ruleSets.Where(r => r.Jurisdiction == market))
        {
            foreach (var rule in ruleSet.Rules)
            {
                foreach (var pattern in rule.Patterns)
                {
                    var patternLower = pattern.ToLowerInvariant();
                    var startIndex = 0;

                    while (true)
                    {
                        startIndex = normalizedText.IndexOf(patternLower, startIndex, StringComparison.Ordinal);
                        if (startIndex == -1) break;

                        if (!IsWholeWordMatch(normalizedText, patternLower, startIndex))
                        {
                            startIndex += 1;
                            continue;
                        }

                        potentialMatches.Add(new PotentialMatch
                        {
                            RuleId = rule.Id,
                            Category = rule.Category,
                            Severity = rule.Severity,
                            Regulation = rule.Regulation,
                            Explanation = rule.Explanation,
                            MatchedPattern = pattern,
                            StartIndex = startIndex,
                            EndIndex = startIndex + patternLower.Length,
                            PatternLength = patternLower.Length
                        });

                        startIndex += 1;
                    }
                }
            }
        }

        // Sort by position, then by pattern length (longest first)
        potentialMatches.Sort((a, b) =>
        {
            if (a.StartIndex != b.StartIndex)
                return a.StartIndex.CompareTo(b.StartIndex);
            return b.PatternLength.CompareTo(a.PatternLength);
        });

        // Filter overlapping matches
        var seenCategories = new HashSet<string>();

        foreach (var match in potentialMatches)
        {
            // Check if this position overlaps with an already-matched region
            var overlaps = false;
            for (var i = match.StartIndex; i < match.EndIndex; i++)
            {
                if (matchedPositions.Contains(i))
                {
                    overlaps = true;
                    break;
                }
            }

            var uniqueKey = $"{match.Category}:{match.MatchedPattern}";

            if (!overlaps && !seenCategories.Contains(uniqueKey))
            {
                // Mark this region as matched
                for (var i = match.StartIndex; i < match.EndIndex; i++)
                {
                    matchedPositions.Add(i);
                }

                seenCategories.Add(uniqueKey);

                findings.Add(new RuleFinding
                {
                    RuleId = match.RuleId,
                    Category = match.Category,
                    Severity = match.Severity,
                    Regulation = match.Regulation,
                    Explanation = match.Explanation,
                    MatchedPattern = match.MatchedPattern
                });
            }
        }

        return findings;
    }

    // pattern needs a word boundary at the start, and if it's 4 chars or less, at the end too. so "eco" won't match inside "economy" but longer patterns can still match stuff like "recycled"
    private static bool IsWholeWordMatch(string text, string pattern, int start)
    {
        if (char.IsLetterOrDigit(pattern[0]) && start > 0 && char.IsLetterOrDigit(text[start - 1]))
            return false;

        var end = start + pattern.Length;
        if (pattern.Length <= 4 && char.IsLetterOrDigit(pattern[^1]) && end < text.Length && char.IsLetterOrDigit(text[end]))
            return false;

        return true;
    }

    private class PotentialMatch
    {
        public string RuleId { get; set; } = "";
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Regulation { get; set; } = "";
        public string Explanation { get; set; } = "";
        public string MatchedPattern { get; set; } = "";
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public int PatternLength { get; set; }
    }
}
