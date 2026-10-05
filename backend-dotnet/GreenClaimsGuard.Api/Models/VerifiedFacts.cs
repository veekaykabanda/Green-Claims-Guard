namespace GreenClaimsGuard.Api.Models;

// How the facts for a check were obtained.
public static class FactsStatus
{
    public const string Loaded = "Loaded";
    public const string NoneOnFile = "NoneOnFile";       // the product exists but nobody has entered facts
    public const string NotApplicable = "NotApplicable"; // the product does not exist yet, so there is nothing to load
    public const string Unavailable = "Unavailable";     // the database could not be reached
}

public class MaterialShare
{
    public string Material { get; set; } = "";
    public decimal Percentage { get; set; }
}

// verified facts for one product, used by the checks
public class VerifiedFacts
{
    public List<MaterialShare> Materials { get; set; } = new();
    public List<string> Certifications { get; set; } = new();
    public string? Origin { get; set; }
    public DateTime? VerifiedAt { get; set; }

    public bool IsEmpty => Materials.Count == 0 && Certifications.Count == 0 && string.IsNullOrWhiteSpace(Origin);
}

public class FactsLoadResult
{
    public string Status { get; set; } = FactsStatus.NotApplicable;
    public VerifiedFacts? Facts { get; set; }
}

// what you send to PUT /api/products/{id}/facts
public class SaveFactsRequest
{
    public List<MaterialShare>? Materials { get; set; }
    public List<string>? Certifications { get; set; }
    public string? Origin { get; set; }

    // why the facts changed, required because every claim gets measured against these
    public string? Reason { get; set; }
}

public class FactsResponse
{
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string Status { get; set; } = FactsStatus.NoneOnFile;
    public List<MaterialShare> Materials { get; set; } = new();
    public List<string> Certifications { get; set; } = new();
    public string? Origin { get; set; }
    public string? VerifiedByUserId { get; set; }
    public DateTime? VerifiedAt { get; set; }
}
