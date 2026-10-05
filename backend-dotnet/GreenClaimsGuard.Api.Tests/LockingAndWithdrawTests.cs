using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// copy locks while in review or published, withdraw doesn't delete it, just adds a new step to the history
[Collection(ApiCollection.Name)]
public class LockingAndWithdrawTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string LockedInReview = "in review";

    private readonly ApiTestFactory _factory;

    public LockingAndWithdrawTests(ApiTestFactory factory)
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

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid product, string text = CleanText) =>
        client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = text,
            issueDecisions = Array.Empty<object>()
        });

    private static async Task<string> StatusOfAsync(HttpClient client, Guid product)
    {
        var products = await client.GetFromJsonAsync<JsonElement>("/api/products");
        return products.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == product).GetProperty("status").GetString()!;
    }

    private static async Task<string> SubmissionStatusOfAsync(HttpClient client, Guid product)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>("/api/my/submissions");
        return rows.EnumerateArray().Single(p => p.GetProperty("productId").GetGuid() == product).GetProperty("status").GetString()!;
    }

    [Fact]
    public async Task WhileInReview_TheWriterCanStillEditItLive_ButTheNameStaysLocked()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Locked in review");
        Assert.True((await SubmitAsync(writer, product)).IsSuccessStatusCode);

        // writer can resubmit their own in-review copy without withdrawing first, goes through the same checks, status stays InReview
        var resubmit = await SubmitAsync(writer, product, CleanText + " Now with pockets.");
        Assert.Equal(HttpStatusCode.OK, resubmit.StatusCode);
        Assert.Equal("InReview", await StatusOfAsync(writer, product));

        var rename = await writer.PatchAsJsonAsync($"/api/products/{product}", new { name = "Renamed while locked" });
        Assert.Equal(HttpStatusCode.Conflict, rename.StatusCode);
        Assert.Contains("locked", (await rename.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
        Assert.Equal("Locked in review", (await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}")).GetProperty("name").GetString());

        // looking isn't editing, running a check is still fine
        var check = await writer.PostAsJsonAsync("/api/analyze", new { text = CleanText, rulesOnly = true, productId = product });
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
    }

    [Fact]
    public async Task WhileInReview_OnlyTheOriginalWriterCanEditItLive_NotAnotherUser()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Locked to its own writer");
        Assert.True((await SubmitAsync(writer, product)).IsSuccessStatusCode);

        // editor can see this in the queue, but only the original writer gets the resubmit exception, everyone else is locked out
        var resubmit = await editor.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = CleanText + " Rewritten by someone else.",
            issueDecisions = Array.Empty<object>()
        });
        var resubmitBody = await resubmit.Content.ReadFromJsonAsync<MarkReadyResponse>();
        Assert.Equal(HttpStatusCode.Conflict, resubmit.StatusCode);
        Assert.Contains(LockedInReview, resubmitBody!.Message);
    }

    [Fact]
    public async Task OncePublished_TheCopyStaysLocked()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Locked when live");
        await SubmitAsync(writer, product);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product })).StatusCode);

        var resubmit = await SubmitAsync(writer, product);
        Assert.Equal(HttpStatusCode.Conflict, resubmit.StatusCode);
        Assert.Contains("published", (await resubmit.Content.ReadFromJsonAsync<MarkReadyResponse>())!.Message);
        Assert.Equal(HttpStatusCode.Conflict, (await writer.PatchAsJsonAsync($"/api/products/{product}", new { name = "Renamed live" })).StatusCode);
    }

    [Fact]
    public async Task Withdrawing_ReturnsTheProductToADraft_LeavesTheQueue_AndFreesTheCopy()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Withdrawn product");
        await SubmitAsync(writer, product);
        Assert.Equal("InReview", await StatusOfAsync(writer, product));
        Assert.Contains(product, (await editor.GetFromJsonAsync<JsonElement>("/api/claims/pending-review")).EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()));

        var withdrawn = await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });

        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        Assert.Equal("Draft", await StatusOfAsync(writer, product));
        Assert.Equal("Withdrawn", await SubmissionStatusOfAsync(writer, product));
        Assert.DoesNotContain(product, (await editor.GetFromJsonAsync<JsonElement>("/api/claims/pending-review")).EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()));

        // editor can't publish something that's no longer submitted
        var publish = await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product });
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);

        // copy's free again, rename works and the writer can submit again
        Assert.Equal(HttpStatusCode.OK, (await writer.PatchAsJsonAsync($"/api/products/{product}", new { name = "Withdrawn and renamed" })).StatusCode);
        Assert.True((await SubmitAsync(writer, product, CleanText + " Revised.")).IsSuccessStatusCode);
        Assert.Equal("InReview", await StatusOfAsync(writer, product));
    }

    [Fact]
    public async Task Withdrawing_WritesAnAuditRow_WithTheTextAndWhoDidIt_AndNothingIsErased()
    {
        var writer = _factory.CreateCopywriter("auth0|withdrawing-writer");
        var product = await CreateProductAsync(writer, "Audited withdrawal");
        await SubmitAsync(writer, product);
        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });

        var entries = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product).OrderBy(e => e.Id).ToListAsync());
        var withdraw = Assert.Single(entries, e => e.Action == AuditActions.Withdraw);
        Assert.Equal("auth0|withdrawing-writer", withdraw.UserId);
        Assert.Equal(AuditOutcomes.Ok, withdraw.Outcome);
        Assert.Equal(CleanText, withdraw.CopySnapshot);
        Assert.Equal(64, withdraw.CopyHash.Length);
        // the submission it withdrew is still sitting in the history
        Assert.Contains(entries, e => e.Action == AuditActions.Submit);

        var steps = await _factory.WithDbAsync(db => db.ComplianceReviews.Where(r => r.ProductId == product).OrderBy(r => r.Id).Select(r => r.OverallStatus).ToListAsync());
        Assert.Equal(new[] { ComplianceStatus.ReadyToPublishSubjectToReview, ComplianceStatus.Withdrawn }, steps);
    }

    [Fact]
    public async Task TheHistory_ShowsWhenAVersionWasWithdrawn()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "History of a withdrawal");
        await SubmitAsync(writer, product);
        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });
        await SubmitAsync(writer, product, CleanText + " Second version.");

        var history = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/history");

        var versions = history.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        Assert.NotEqual(JsonValueKind.Null, versions[0].GetProperty("withdrawnAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, versions[0].GetProperty("publishedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, versions[1].GetProperty("withdrawnAt").ValueKind);
        Assert.Equal("InReview", history.GetProperty("status").GetString());
    }

    [Fact]
    public async Task OnlyTheOwner_CanWithdraw()
    {
        var owner = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(owner, "Not yours to withdraw");
        await SubmitAsync(owner, product);

        var other = await _factory.CreateCopywriter(NewUser()).PostAsJsonAsync("/api/claims/withdraw", new { productId = product });
        var editor = await _factory.CreateSeniorEditor().PostAsJsonAsync("/api/claims/withdraw", new { productId = product });

        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, editor.StatusCode);
        Assert.Equal("InReview", await StatusOfAsync(owner, product));
    }

    [Fact]
    public async Task ThereMustBeSomethingWaitingToWithdraw()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var draft = await CreateProductAsync(writer, "Never submitted");
        var live = await CreateProductAsync(writer, "Already live");
        await SubmitAsync(writer, live);
        await editor.PostAsJsonAsync("/api/claims/publish", new { productId = live });
        var twice = await CreateProductAsync(writer, "Withdrawn twice");
        await SubmitAsync(writer, twice);
        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = twice });

        Assert.Equal(HttpStatusCode.Conflict, (await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = draft })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = live })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = twice })).StatusCode);
    }

    [Fact]
    public async Task Withdraw_NeedsASignedInUserAndAProduct()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateAnonymousClient().PostAsJsonAsync("/api/claims/withdraw", new { productId = Guid.NewGuid() })).StatusCode);
        var writer = _factory.CreateCopywriter(NewUser());
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.PostAsJsonAsync("/api/claims/withdraw", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = Guid.NewGuid() })).StatusCode);
    }
}
