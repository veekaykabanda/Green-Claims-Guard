using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

[Collection(ApiCollection.Name)]
public class ProductWorkflowTests
{
    private readonly ApiTestFactory _factory;

    public ProductWorkflowTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid productId, string description) =>
        client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId,
            finalDescription = description,
            issueDecisions = Array.Empty<object>()
        });

    [Fact]
    public async Task SameProductNameTwice_CreatesTwoProducts_AndNeitherOverwritesTheOther()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        const string sharedName = "Same name jacket";

        var first = await CreateProductAsync(copywriter, sharedName);
        var second = await CreateProductAsync(copywriter, sharedName);
        Assert.NotEqual(first, second);

        await SubmitAsync(copywriter, first, "First jacket description.");
        await SubmitAsync(copywriter, second, "Second jacket description.");

        var pending = await seniorEditor.GetFromJsonAsync<List<PendingReviewItem>>("/api/claims/pending-review");

        var mine = pending!.Where(p => p.ProductName == sharedName).ToList();
        Assert.Equal(2, mine.Count);
        Assert.Contains(mine, p => p.ProductId == first && p.FinalDescription == "First jacket description.");
        Assert.Contains(mine, p => p.ProductId == second && p.FinalDescription == "Second jacket description.");
    }

    [Fact]
    public async Task PublishingOneProduct_RemovesOnlyThatProductFromPendingReview()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();
        const string sharedName = "Publish one of two";

        var first = await CreateProductAsync(copywriter, sharedName);
        var second = await CreateProductAsync(copywriter, sharedName);
        await SubmitAsync(copywriter, first, "Description one.");
        await SubmitAsync(copywriter, second, "Description two.");

        var publish = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId = first });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var pending = await seniorEditor.GetFromJsonAsync<List<PendingReviewItem>>("/api/claims/pending-review");
        var mine = pending!.Where(p => p.ProductName == sharedName).ToList();
        Assert.Single(mine);
        Assert.Equal(second, mine[0].ProductId);
    }

    [Fact]
    public async Task Submit_WithAnUnknownProductId_IsRejected()
    {
        var response = await SubmitAsync(_factory.CreateCopywriter(), Guid.NewGuid(), "Some description.");
        var result = await response.Content.ReadFromJsonAsync<MarkReadyResponse>();

        Assert.Equal(ComplianceStatus.ChangesRequired, result!.OverallStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("Unknown product"));
    }

    [Fact]
    public async Task Submit_WithNoProductId_IsRejected()
    {
        var response = await SubmitAsync(_factory.CreateCopywriter(), Guid.Empty, "Some description.");
        var result = await response.Content.ReadFromJsonAsync<MarkReadyResponse>();

        Assert.Equal(ComplianceStatus.ChangesRequired, result!.OverallStatus);
        Assert.Contains(result.ValidationErrors, e => e.Contains("productId is required"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreatingAProduct_WithoutAName_IsRejected(string name)
    {
        var response = await _factory.CreateCopywriter().PostAsJsonAsync("/api/products", new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreatingAProduct_RecordsWhoCreatedIt()
    {
        var id = await CreateProductAsync(_factory.CreateCopywriter("auth0|creator-42"), "Traceable product");

        var creator = await _factory.WithDbAsync(db =>
            db.Products.Where(p => p.Id == id).Select(p => p.CreatedByUserId).SingleAsync());

        Assert.Equal("auth0|creator-42", creator);
    }
}
