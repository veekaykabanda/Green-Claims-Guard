using System.Text.Json;
using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// records what changed in a product's verified facts and why
public static class FactsAudit
{
    public static object Snapshot(List<MaterialShare> materials, List<string> certifications, string? origin) => new
    {
        materials = materials.OrderByDescending(m => m.Percentage).Select(m => new { m.Material, m.Percentage }),
        certifications = certifications.OrderBy(c => c, StringComparer.OrdinalIgnoreCase),
        origin,
    };

    public static AuditEntry Row(Guid productId, string? userId, string reason, object before, object after) => new()
    {
        ProductId = productId,
        Action = AuditActions.FactsChange,
        Outcome = AuditOutcomes.Ok,
        CopySnapshot = string.Empty,
        CopyHash = AuditEntryFactory.HashOf(string.Empty),
        Justification = reason,
        Detail = JsonSerializer.Serialize(new { before, after }),
        UserId = userId,
    };
}
