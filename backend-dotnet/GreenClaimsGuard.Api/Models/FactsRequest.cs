namespace GreenClaimsGuard.Api.Models;

// just a note asking the editor to check facts, doesn't change anything and the checker and AI never see it
public class FactsRequest
{
    public int Id { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public string RequestedByUserId { get; set; } = "";
    public string Note { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RequestFactsRequest
{
    public string? Note { get; set; }
}
