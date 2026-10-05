using System.Security.Claims;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Security;

// creator or senior editor can open it, copywriters only see their own, old no-creator products go to senior editors
public sealed class ProductOwnerRequirement : IAuthorizationRequirement
{
}

public sealed class ProductOwnerHandler : AuthorizationHandler<ProductOwnerRequirement, Product>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, ProductOwnerRequirement requirement, Product product)
    {
        if (Personas.Of(context.User) == Personas.SeniorEditor)
        {
            context.Succeed(requirement);
        }
        else
        {
            var me = CurrentUser.GetId(context.User);
            if (me is not null && product.CreatedByUserId == me)
            {
                context.Succeed(requirement);
            }
        }

        return Task.CompletedTask;
    }
}

public enum ProductAccessOutcome
{
    Ok,
    NotFound,
    Forbidden,
}

public sealed record ProductAccessResult(ProductAccessOutcome Outcome, Product? Product)
{
    public bool IsOk => Outcome == ProductAccessOutcome.Ok;

    // what to send back when access is denied
    public IResult Failure => Outcome == ProductAccessOutcome.Forbidden ? Problems.Forbidden() : Problems.NotFound();
}

// only way endpoints open a product, load it, check access, only hand it over if allowed
public class ProductAccess
{
    private static readonly ProductOwnerRequirement Requirement = new();

    private readonly AppDbContext _db;
    private readonly IAuthorizationService _authorization;

    public ProductAccess(AppDbContext db, IAuthorizationService authorization)
    {
        _db = db;
        _authorization = authorization;
    }

    // track means you're about to edit it, withFacts also loads materials and certifications
    public async Task<ProductAccessResult> OpenAsync(Guid id, ClaimsPrincipal user, bool track = false, bool withFacts = false)
    {
        IQueryable<Product> query = _db.Products;
        if (!track) query = query.AsNoTracking();
        if (withFacts) query = query.Include(p => p.Materials).Include(p => p.Certifications);

        var product = await query.FirstOrDefaultAsync(p => p.Id == id);
        if (product is null) return new ProductAccessResult(ProductAccessOutcome.NotFound, null);

        var decision = await _authorization.AuthorizeAsync(user, product, Requirement);
        return decision.Succeeded
            ? new ProductAccessResult(ProductAccessOutcome.Ok, product)
            : new ProductAccessResult(ProductAccessOutcome.Forbidden, null);
    }

    // owner only, not even senior editors, for your own drafts and submissions
    public async Task<ProductAccessResult> OpenOwnedAsync(Guid id, ClaimsPrincipal user, bool track = false)
    {
        var opened = await OpenAsync(id, user, track);
        if (!opened.IsOk) return opened;

        var me = CurrentUser.GetId(user);
        return me is not null && opened.Product!.CreatedByUserId == me
            ? opened
            : new ProductAccessResult(ProductAccessOutcome.Forbidden, null);
    }
}
