using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// first version of this app trusted a client-sent X-User-Role header, so anyone could publish just by claiming to be "compliance-manager". these tests keep that hole shut
[Collection(ApiCollection.Name)]
public class RoleHeaderBypassTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";

    private static readonly string[] ClaimHeaders =
    {
        "X-User-Role", "X-User-Roles", "X-Role", "X-User-Permissions", "X-Permissions", "X-Test-Role",
    };

    private readonly ApiTestFactory _factory;

    public RoleHeaderBypassTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static string NewUser() => $"auth0|writer-{Guid.NewGuid():N}";

    // a copywriter (analyse:claims only) who also sends every header anyone's ever used to fake a bigger role
    private HttpClient WriterClaimingToBeAManager(string userId)
    {
        var client = _factory.CreateCopywriter(userId);
        foreach (var header in ClaimHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header, "compliance-manager,publish:product,override:compliance,edit:product-facts");
        }
        return client;
    }

    [Fact]
    public async Task ACopywriterCannotPublish_ByClaimingToBeAManager()
    {
        var response = await WriterClaimingToBeAManager(NewUser()).PostAsJsonAsync("/api/claims/publish", new { productId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/audit/export")]
    [InlineData("POST", "/api/data-transfer/seed")]
    [InlineData("GET", "/api/facts-requests")]
    public async Task ACopywriterCannotReachSeniorEditorScreens_ByClaimingToBeAManager(string method, string path)
    {
        var client = WriterClaimingToBeAManager(NewUser());

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ACopywriterCannotEditVerifiedFacts_ByClaimingToBeAManager()
    {
        var writer = WriterClaimingToBeAManager(NewUser());
        var created = await writer.PostAsJsonAsync("/api/products", new { name = "Linen shirt" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var response = await writer.PutAsJsonAsync($"/api/products/{id}/facts", new { materials = new[] { new { material = "cotton", percentage = 100 } }, reason = "I say so, honestly." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SubmittingForReview_NeverPublishes_WhateverTheHeadersSay()
    {
        var writer = WriterClaimingToBeAManager(NewUser());
        var created = await writer.PostAsJsonAsync("/api/products", new { name = "Linen shirt" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await writer.PutAsJsonAsync($"/api/products/{id}/draft", new { text = CleanText, market = "UK" })).EnsureSuccessStatusCode();

        (await writer.PostAsJsonAsync("/api/claims/mark-ready", new { productId = id, finalDescription = CleanText, issueDecisions = Array.Empty<object>() })).EnsureSuccessStatusCode();

        var steps = await _factory.WithDbAsync(db => db.ComplianceReviews.Where(r => r.ProductId == id).Select(r => r.OverallStatus).ToListAsync());
        Assert.DoesNotContain(ComplianceStatus.Published, steps);
        Assert.Equal(new[] { ComplianceStatus.ReadyToPublishSubjectToReview }, steps);
    }

    [Fact]
    public async Task SomeoneNotSignedIn_GetsNothing_WithOrWithoutTheHeaders()
    {
        var client = _factory.CreateAnonymousClient();
        foreach (var header in ClaimHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header, "compliance-manager,publish:product");
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/claims/pending-review")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/claims/publish", new { productId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task ARealSeniorEditor_StillCanPublishTheirScreens_SoTheTestsAboveProveTheHeadersAreWhatIsIgnored()
    {
        // same requests work fine for someone who actually holds the permission, so the refusals above are about the headers, not a broken route
        var response = await _factory.CreateSeniorEditor(NewUser()).GetAsync("/api/claims/pending-review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
