using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Endpoints;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// drafts are private, never audited, lock while the copy's in review, deleted on submit, and auto-removed after 90 days unused
[Collection(ApiCollection.Name)]
public class DraftTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";

    private readonly ApiTestFactory _factory;

    public DraftTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static string NewUser() => $"auth0|writer-{Guid.NewGuid():N}";

    private static async Task<Guid> CreateProductAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/products", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid product, string text, string market = "UK") =>
        client.PutAsJsonAsync($"/api/products/{product}/draft", new { text, market });

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid product, string text) =>
        client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = text,
            issueDecisions = Array.Empty<object>()
        });

    [Fact]
    public async Task ADraftCanBeSaved_ReadBack_AndReplaced()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Drafted product");

        var first = await SaveAsync(writer, product, "First words of the description.", "EU");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("saved").GetBoolean());

        var read = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/draft");
        Assert.Equal("First words of the description.", read.GetProperty("text").GetString());
        Assert.Equal("EU", read.GetProperty("market").GetString());
        Assert.Equal("Drafted product", read.GetProperty("productName").GetString());
        var firstSaved = read.GetProperty("savedAt").GetDateTime();

        await Task.Delay(20);
        await SaveAsync(writer, product, "Second, longer words of the description.", "UK");
        var replaced = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/draft");
        Assert.Equal("Second, longer words of the description.", replaced.GetProperty("text").GetString());
        Assert.Equal("UK", replaced.GetProperty("market").GetString());
        Assert.True(replaced.GetProperty("savedAt").GetDateTime() > firstSaved);

        // One draft per product, not one per save.
        var drafts = await _factory.WithDbAsync(db => db.ProductDrafts.CountAsync(d => d.ProductId == product));
        Assert.Equal(1, drafts);
    }

    [Fact]
    public async Task MyDrafts_ListsOnlyMyOwn_WithAPreview()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var other = _factory.CreateCopywriter(NewUser());
        var mine = await CreateProductAsync(writer, "My drafted product");
        var theirs = await CreateProductAsync(other, "Their drafted product");
        await SaveAsync(writer, mine, new string('a', 300));
        await SaveAsync(other, theirs, "Somebody else's private words.");

        var list = await writer.GetFromJsonAsync<JsonElement>("/api/my/drafts");

        var row = Assert.Single(list.EnumerateArray(), d => d.GetProperty("productId").GetGuid() == mine);
        Assert.Equal("My drafted product", row.GetProperty("productName").GetString());
        Assert.True(row.GetProperty("preview").GetString()!.Length < 300);
        Assert.DoesNotContain(list.EnumerateArray(), d => d.GetProperty("productId").GetGuid() == theirs);
    }

    [Fact]
    public async Task ADraftIsPrivate_EvenFromASeniorEditor()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Private draft");
        await SaveAsync(writer, product, "Words only the writer may see.");

        foreach (var outsider in new[] { _factory.CreateCopywriter(NewUser()), _factory.CreateSeniorEditor() })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/products/{product}/draft")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await SaveAsync(outsider, product, "Overwritten!")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.DeleteAsync($"/api/products/{product}/draft")).StatusCode);
        }

        var read = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/draft");
        Assert.Equal("Words only the writer may see.", read.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ADraftCanBeDeleted_AndDeletingNothingIsFine()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Deleted draft");
        await SaveAsync(writer, product, "Soon gone.");

        Assert.Equal(HttpStatusCode.NoContent, (await writer.DeleteAsync($"/api/products/{product}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{product}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await writer.DeleteAsync($"/api/products/{product}/draft")).StatusCode);
    }

    [Fact]
    public async Task SavingEmptyText_MeansNoDraft()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Emptied draft");
        await SaveAsync(writer, product, "Something.");

        var cleared = await SaveAsync(writer, product, "   ");

        Assert.False((await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("saved").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{product}/draft")).StatusCode);
    }

    [Fact]
    public async Task ADraftNeedsARealProduct_AndSaneInput()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Validated draft");

        Assert.Equal(HttpStatusCode.NotFound, (await SaveAsync(writer, Guid.NewGuid(), "No such product.")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(writer, product, new string('x', DraftEndpoints.MaxTextLength + 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SaveAsync(writer, product, "Fine text.", "US")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.PutAsJsonAsync($"/api/products/{product}/draft", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(writer, product, new string('x', DraftEndpoints.MaxTextLength))).StatusCode);
    }

    [Fact]
    public async Task ASignedOutVisitor_CannotTouchDrafts()
    {
        var anonymous = _factory.CreateAnonymousClient();
        var some = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SaveAsync(anonymous, some, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/products/{some}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/my/drafts")).StatusCode);
    }

    [Fact]
    public async Task SavingADraft_NeverWritesToTheAuditTrail()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Quiet draft");

        for (var i = 0; i < 6; i++) await SaveAsync(writer, product, $"Autosave number {i} of the description.");
        await writer.DeleteAsync($"/api/products/{product}/draft");

        var rows = await _factory.WithDbAsync(db => db.AuditLedger.CountAsync(e => e.ProductId == product));
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task ACopyThatIsInReview_CannotBeDrafted_UntilItIsWithdrawn()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Locked draft");
        Assert.True((await SubmitAsync(writer, product, CleanText)).IsSuccessStatusCode);

        var locked = await SaveAsync(writer, product, "Trying to sneak an edit in.");
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);
        Assert.Contains("locked", (await locked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());

        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });
        Assert.Equal(HttpStatusCode.OK, (await SaveAsync(writer, product, "A revised version.")).StatusCode);
    }

    [Fact]
    public async Task Submitting_ReplacesTheDraft()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Submitted draft");
        await SaveAsync(writer, product, CleanText);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync($"/api/products/{product}/draft")).StatusCode);

        Assert.True((await SubmitAsync(writer, product, CleanText)).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{product}/draft")).StatusCode);
    }

    [Fact]
    public async Task ARefusedSubmission_KeepsTheDraft()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Refused submission");
        const string critical = "Our new range is made from sustainable bamboo fabric.";
        await SaveAsync(writer, product, critical);

        var refused = await SubmitAsync(writer, product, critical);
        Assert.False((await refused.Content.ReadFromJsonAsync<MarkReadyResponse>())!.ReadyToPublishSubjectToReview);

        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync($"/api/products/{product}/draft")).StatusCode);
    }

    [Fact]
    public async Task DraftsUntouchedFor90Days_AreDeleted_AndNewerOnesKept()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var old = await CreateProductAsync(writer, "Abandoned draft");
        var recent = await CreateProductAsync(writer, "Recent draft");
        await SaveAsync(writer, old, "Nobody has touched this.");
        await SaveAsync(writer, recent, "Someone touched this lately.");

        var now = DateTime.UtcNow;
        await _factory.WithDbAsync(async db =>
        {
            (await db.ProductDrafts.FindAsync(old))!.SavedAt = now.AddDays(-91);
            (await db.ProductDrafts.FindAsync(recent))!.SavedAt = now.AddDays(-89);
            await db.SaveChangesAsync();
            return 0;
        });

        var removed = await _factory.WithDbAsync(db => DraftRetention.PurgeAsync(db, now));

        Assert.True(removed >= 1);
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{old}/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.GetAsync($"/api/products/{recent}/draft")).StatusCode);
    }

    [Fact]
    public void TheDefaultRetentionIs90Days()
    {
        Assert.Equal(90, DraftRetention.DefaultDays);
    }
}
