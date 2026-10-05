namespace GreenClaimsGuard.Api.Models;

// Id is what makes a product unique, not the name, two products can share a name
public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Sku { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedByUserId { get; set; }

    // senior editor enters the real facts here, every claim in the copy gets measured against this
    public string? Origin { get; set; }
    public string? FactsVerifiedByUserId { get; set; }
    public DateTime? FactsVerifiedAt { get; set; }
    public List<ProductMaterial> Materials { get; set; } = new();
    public List<ProductCertification> Certifications { get; set; } = new();
}

public class ProductMaterial
{
    public int Id { get; set; }
    public Guid ProductId { get; set; }
    public string Material { get; set; } = "";
    public decimal Percentage { get; set; }
}

public class ProductCertification
{
    public int Id { get; set; }
    public Guid ProductId { get; set; }
    public string Name { get; set; } = "";
}

public class CreateProductRequest
{
    public string? Name { get; set; }
    public string? Sku { get; set; }
}

public class RenameProductRequest
{
    public string? Name { get; set; }
}
