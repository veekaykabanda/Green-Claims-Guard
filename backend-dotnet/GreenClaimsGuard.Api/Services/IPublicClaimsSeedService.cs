using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public interface IPublicClaimsSeedService
{
    Task<PublicClaimsSeedImportResponse> ImportAsync(string csvPath);
    Task<PublicClaimsSeedImportResponse> SeedIfEmptyAsync(string csvPath);
}
