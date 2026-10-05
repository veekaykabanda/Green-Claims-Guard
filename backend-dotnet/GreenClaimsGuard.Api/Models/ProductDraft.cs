namespace GreenClaimsGuard.Api.Models;

// scratch space for a draft that hasn't been submitted, not the official record, one per product, clears after 90 days untouched
public class ProductDraft
{
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public string Text { get; set; } = "";
    public string Market { get; set; } = Markets.Uk;
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

public class SaveDraftRequest
{
    public string? Text { get; set; }
    public string? Market { get; set; }
}
