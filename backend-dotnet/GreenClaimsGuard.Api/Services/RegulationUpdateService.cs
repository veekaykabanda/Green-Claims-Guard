using System.Xml.Linq;

namespace GreenClaimsGuard.Api.Services;

public class RegulationUpdateService : IRegulationUpdateService
{
    private readonly HttpClient _http;
    private readonly ILogger<RegulationUpdateService> _logger;

    private List<RegulationUpdate>? _cache;
    private DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(6);

    // these are real gov.uk atom feed urls, double checked
    private static readonly (string Url, string Label)[] Feeds =
    [
        ("https://www.gov.uk/search/news-and-communications.atom?keywords=green+claims+code", "GOV.UK"),
        ("https://www.gov.uk/search/news-and-communications.atom?keywords=greenwashing+CMA", "CMA"),
    ];

    // matches a keyword to a friendly claim type label
    private static readonly (string Keyword, string Label)[] ClaimTypeMap =
    [
        ("carbon neutral", "Carbon neutral"),
        ("net zero", "Net zero"),
        ("carbon negative", "Carbon negative"),
        ("organic", "Organic"),
        ("recycled", "Recycled content"),
        ("bamboo", "Bamboo fabric"),
        ("sustainable", "Sustainability claims"),
        ("eco-friendly", "Eco-friendly"),
        ("biodegradable", "Biodegradable"),
        ("compostable", "Compostable"),
        ("vegan leather", "Vegan leather"),
        ("fashion", "Fashion & apparel"),
        ("greenwashing", "Greenwashing"),
        ("misleading", "Misleading claims"),
    ];

    // real CMA/ASA items we always show, even if the live feed is down
    private static readonly List<RegulationUpdate> PinnedUpdates =
    [
        new()
        {
            Id = "pinned-cma-fashion-2023",
            Title = "CMA secures green pledges from ASOS, Boohoo and Asda",
            Url = "https://www.gov.uk/government/news/cma-secures-green-pledges-from-major-fashion-brands",
            PublishedDate = new DateTimeOffset(2023, 9, 11, 0, 0, 0, TimeSpan.Zero),
            Summary = "ASOS, Boohoo and Asda have agreed to overhaul how they promote their green credentials after a CMA investigation found potentially misleading environmental claims in the fashion sector.",
            Source = "CMA",
            AffectedClaimTypes = ["Fashion & apparel", "Sustainability claims", "Greenwashing"]
        },
        new()
        {
            Id = "pinned-cma-green-claims-code-2021",
            Title = "CMA Green Claims Code: 6 principles for environmental claims",
            Url = "https://www.gov.uk/government/publications/green-claims-code-making-environmental-claims",
            PublishedDate = new DateTimeOffset(2021, 9, 20, 0, 0, 0, TimeSpan.Zero),
            Summary = "The CMA published its Green Claims Code setting out six principles businesses must follow when making environmental claims. Applies to all sectors including fashion and ecommerce.",
            Source = "CMA",
            AffectedClaimTypes = ["Sustainability claims", "Greenwashing", "Misleading claims"]
        },
        new()
        {
            Id = "pinned-asa-bamboo-2021",
            Title = "ASA ruling: 'bamboo' fabric claims found misleading",
            Url = "https://www.asa.org.uk/advice-online/environmental-claims.html",
            PublishedDate = new DateTimeOffset(2021, 6, 16, 0, 0, 0, TimeSpan.Zero),
            Summary = "The ASA upheld complaints against brands describing products as 'bamboo fabric' when the material is bamboo-derived viscose. Claims must accurately describe the manufacturing process.",
            Source = "ASA",
            AffectedClaimTypes = ["Bamboo fabric", "Fashion & apparel", "Misleading claims"]
        },
        new()
        {
            Id = "pinned-cma-carbon-neutral-2023",
            Title = "CMA investigation into 'carbon neutral' and 'net zero' claims",
            Url = "https://www.gov.uk/cma-cases/environmental-claims-on-household-essentials",
            PublishedDate = new DateTimeOffset(2023, 1, 26, 0, 0, 0, TimeSpan.Zero),
            Summary = "The CMA launched a review of environmental claims on products to assess whether terms like 'carbon neutral', 'net zero' and 'compostable' mislead consumers. Applies across all product sectors.",
            Source = "CMA",
            AffectedClaimTypes = ["Carbon neutral", "Net zero", "Greenwashing"]
        },
    ];

    public RegulationUpdateService(ILogger<RegulationUpdateService> logger)
    {
        _logger = logger;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders = { { "User-Agent", "GreenClaimsGuard/1.0" } }
        };
    }

    public async Task<List<RegulationUpdate>> GetUpdatesAsync()
    {
        if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiry)
            return _cache;

        // Start with pinned items
        var results = new List<RegulationUpdate>(PinnedUpdates);

        // add live feed items too, if a feed fails just skip it quietly
        foreach (var (feedUrl, label) in Feeds)
        {
            try
            {
                var xml = await _http.GetStringAsync(feedUrl);
                var parsed = ParseAtomFeed(xml, label);
                // skip it if we already have it pinned
                foreach (var item in parsed)
                {
                    if (!results.Any(r => r.Id == item.Id))
                        results.Add(item);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not fetch regulation feed {Label}: {Error}", label, ex.Message);
            }
        }

        results.Sort((a, b) => b.PublishedDate.CompareTo(a.PublishedDate));
        if (results.Count > 30) results = results[..30];

        _cache = results;
        _cacheExpiry = DateTimeOffset.UtcNow.Add(_cacheDuration);

        _logger.LogInformation("Regulation updates refreshed: {Count} items", results.Count);

        return results;
    }

    private static List<RegulationUpdate> ParseAtomFeed(string xml, string sourceLabel)
    {
        var ns = XNamespace.Get("http://www.w3.org/2005/Atom");
        var doc = XDocument.Parse(xml);
        var updates = new List<RegulationUpdate>();
        var cutoff = DateTimeOffset.UtcNow.AddMonths(-18);

        foreach (var entry in doc.Descendants(ns + "entry"))
        {
            var title = entry.Element(ns + "title")?.Value.Trim() ?? "";
            var link = entry.Elements(ns + "link")
                .FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate" || l.Attribute("rel") == null)
                ?.Attribute("href")?.Value ?? "";
            var publishedRaw = entry.Element(ns + "published")?.Value
                             ?? entry.Element(ns + "updated")?.Value ?? "";
            var summary = StripHtml(entry.Element(ns + "summary")?.Value ?? "");
            var id = entry.Element(ns + "id")?.Value.Trim() ?? link;

            if (!DateTimeOffset.TryParse(publishedRaw, out var published))
                published = DateTimeOffset.UtcNow;

            if (published < cutoff) continue;
            if (string.IsNullOrWhiteSpace(title)) continue;

            var searchText = $"{title} {summary}".ToLowerInvariant();
            var affected = ClaimTypeMap
                .Where(ct => searchText.Contains(ct.Keyword))
                .Select(ct => ct.Label)
                .Distinct()
                .ToList();

            updates.Add(new RegulationUpdate
            {
                Id = id,
                Title = title,
                Url = link,
                PublishedDate = published,
                Summary = summary.Length > 300 ? summary[..300] + "…" : summary,
                Source = sourceLabel,
                AffectedClaimTypes = affected
            });
        }

        return updates;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        return System.Text.RegularExpressions.Regex
            .Replace(html, "<[^>]+>", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&nbsp;", " ");
    }
}
