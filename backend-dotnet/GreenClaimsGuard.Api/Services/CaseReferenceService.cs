using System.Text.Json;
using System.Text.Json.Serialization;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface ICaseReferenceService
{
    List<CaseReference> GetCasesForRuleIds(IEnumerable<string> ruleIds);
}

public class CaseReferenceService : ICaseReferenceService
{
    private readonly List<CaseEntry> _cases;
    private readonly ILogger<CaseReferenceService> _logger;

    public CaseReferenceService(ILogger<CaseReferenceService> logger, IWebHostEnvironment env)
    {
        _logger = logger;
        _cases = new List<CaseEntry>();

        var path = Path.Combine(env.ContentRootPath, "Rules", "fashion_cases.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("fashion_cases.json not found at {Path}", path);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = JsonSerializer.Deserialize<List<CaseEntry>>(json, options);
            if (entries != null)
            {
                _cases.AddRange(entries);
                _logger.LogInformation("Loaded {Count} case references from fashion_cases.json", _cases.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load fashion_cases.json");
        }
    }

    public List<CaseReference> GetCasesForRuleIds(IEnumerable<string> ruleIds)
    {
        var ruleIdSet = new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase);

        return _cases
            .Where(c => c.RuleIds.Any(r => ruleIdSet.Contains(r)))
            .Select(c => new CaseReference
            {
                Id = c.Id,
                Brand = c.Brand,
                Year = c.Year,
                Body = c.Body,
                Outcome = c.Outcome,
                Summary = c.Summary
            })
            .ToList();
    }

    private class CaseEntry
    {
        public string Id { get; set; } = "";
        public string Brand { get; set; } = "";
        public int Year { get; set; }
        public string Body { get; set; } = "";
        public string Outcome { get; set; } = "";
        public string Summary { get; set; } = "";
        [JsonPropertyName("ruleIds")]
        public List<string> RuleIds { get; set; } = new();
    }
}
