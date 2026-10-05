using System.Collections.Concurrent;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

public interface IProductFactsProvider
{
    Task<FactsLoadResult> LoadAsync(Guid? productId);

    // call this after facts change so the next check gets the new ones straight away
    void Invalidate(Guid productId);
}

public class ProductFactsProvider : IProductFactsProvider
{
    // live typing checks fire a lot, cache for a few seconds so we're not hitting the db every time
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDbService _dbService;
    private readonly ILogger<ProductFactsProvider> _logger;
    private readonly ConcurrentDictionary<Guid, (DateTime ExpiresAt, FactsLoadResult Result)> _cache = new();

    public ProductFactsProvider(IServiceScopeFactory scopeFactory, IDbService dbService, ILogger<ProductFactsProvider> logger)
    {
        _scopeFactory = scopeFactory;
        _dbService = dbService;
        _logger = logger;
    }

    public void Invalidate(Guid productId) => _cache.TryRemove(productId, out _);

    public async Task<FactsLoadResult> LoadAsync(Guid? productId)
    {
        if (productId is null || productId == Guid.Empty)
            return new FactsLoadResult { Status = FactsStatus.NotApplicable };

        if (!_dbService.IsConfigured)
            return new FactsLoadResult { Status = FactsStatus.Unavailable };

        if (_cache.TryGetValue(productId.Value, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            return cached.Result;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var product = await db.Products.AsNoTracking()
                .Include(p => p.Materials)
                .Include(p => p.Certifications)
                .FirstOrDefaultAsync(p => p.Id == productId.Value);

            FactsLoadResult result;
            if (product is null)
            {
                result = new FactsLoadResult { Status = FactsStatus.NotApplicable };
            }
            else
            {
                var facts = new VerifiedFacts
                {
                    Materials = product.Materials.OrderByDescending(m => m.Percentage)
                        .Select(m => new MaterialShare { Material = m.Material, Percentage = m.Percentage }).ToList(),
                    Certifications = product.Certifications.Select(c => c.Name).ToList(),
                    Origin = product.Origin,
                    VerifiedAt = product.FactsVerifiedAt
                };
                result = facts.IsEmpty
                    ? new FactsLoadResult { Status = FactsStatus.NoneOnFile }
                    : new FactsLoadResult { Status = FactsStatus.Loaded, Facts = facts };
            }

            _cache[productId.Value] = (DateTime.UtcNow + CacheLifetime, result);
            return result;
        }
        catch (Exception ex)
        {
            // don't cache a failure, just try again next time
            _logger.LogError(ex, "Could not load verified facts for product {ProductId}", productId);
            return new FactsLoadResult { Status = FactsStatus.Unavailable };
        }
    }
}
