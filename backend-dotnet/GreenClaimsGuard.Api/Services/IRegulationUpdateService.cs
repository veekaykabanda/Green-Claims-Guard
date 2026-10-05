namespace GreenClaimsGuard.Api.Services;

public interface IRegulationUpdateService
{
    Task<List<RegulationUpdate>> GetUpdatesAsync();
}

public record RegulationUpdate
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public DateTimeOffset PublishedDate { get; init; }
    public string Summary { get; init; } = "";
    public string Source { get; init; } = "";
    public List<string> AffectedClaimTypes { get; init; } = new();
}
