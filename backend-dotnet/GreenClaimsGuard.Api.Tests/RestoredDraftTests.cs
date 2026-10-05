using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// when a submission comes back to the writer (withdraw or send-back), the server hands them their text back as a draft to carry on from
[Collection(ApiCollection.Name)]
public class RestoredDraftTests
{
    private const string Submitted = "A plain cotton shirt with a relaxed fit.";

    private readonly ApiTestFactory _factory;

    public RestoredDraftTests(ApiTestFactory factory)
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

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, Guid product, string? text = Submitted, string market = "UK") =>
        client.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId = product,
            finalDescription = text,
            market,
            issueDecisions = Array.Empty<object>()
        });

    [Fact]
    public async Task Withdrawing_GivesTheWriterTheirSubmittedTextBackAsADraft()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Restored on withdraw");
        await SubmitAsync(writer, product, Submitted, "EU");
        Assert.Equal(HttpStatusCode.NotFound, (await writer.GetAsync($"/api/products/{product}/draft")).StatusCode);

        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });

        var draft = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/draft");
        Assert.Equal(Submitted, draft.GetProperty("text").GetString());
        Assert.Equal("EU", draft.GetProperty("market").GetString());
        var listed = await writer.GetFromJsonAsync<JsonElement>("/api/my/drafts");
        Assert.Contains(listed.EnumerateArray(), d => d.GetProperty("productId").GetGuid() == product);
    }

    [Fact]
    public async Task ASendBack_GivesTheWriterTheirSubmittedTextBackAsADraft()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Restored on send back");
        await SubmitAsync(writer, product, Submitted, "UK");

        await _factory.CreateSeniorEditor().PostAsJsonAsync("/api/claims/send-back", new
        {
            productId = product, reasonCategory = "not_accurate", comment = "The claim needs a certificate."
        });

        var draft = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/draft");
        Assert.Equal(Submitted, draft.GetProperty("text").GetString());
        // draft belongs to the writer, not the editor who sent it back
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.CreateSeniorEditor().GetAsync($"/api/products/{product}/draft")).StatusCode);
    }

    [Fact]
    public async Task ARestoredDraft_IsNeverAudited_AndNeverOverwritesNewerWork()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Restored quietly");
        await SubmitAsync(writer, product);
        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });

        var entries = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product).Select(e => e.Action).ToListAsync());
        Assert.Equal(new[] { AuditActions.Submit, AuditActions.Withdraw }, entries);

        // writer keeps working, submits again, gets sent back again, newer work never gets overwritten
        await writer.PutAsJsonAsync($"/api/products/{product}/draft", new { text = "Newer words the writer typed.", market = "UK" });
        await _factory.WithDbAsync(async db =>
        {
            db.ComplianceReviews.Add(new ComplianceReview
            {
                ProductId = product, FinalDescription = Submitted, OverallStatus = ComplianceStatus.ReadyToPublishSubjectToReview,
                Market = "UK", DecisionsJson = "[]"
            });
            await db.SaveChangesAsync();
            return 0;
        });
        await _factory.CreateSeniorEditor().PostAsJsonAsync("/api/claims/send-back", new
        {
            productId = product, reasonCategory = "vague_or_unclear", comment = "Say which part is meant."
        });

        var draft = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/draft");
        Assert.Equal("Newer words the writer typed.", draft.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AfterAWithdrawal_TheWriterEditsTheDraftAndSubmitsIt()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Edit and resubmit");
        await SubmitAsync(writer, product);
        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product });
        await writer.PutAsJsonAsync($"/api/products/{product}/draft", new { text = Submitted + " Machine washable.", market = "UK" });

        var again = await (await SubmitAsync(writer, product, "ignored client text")).Content.ReadFromJsonAsync<MarkReadyResponse>();

        Assert.True(again!.ReadyToPublishSubjectToReview);
        Assert.True(again.UsedDraft);
        var review = await _factory.WithDbAsync(db => db.ComplianceReviews.Where(r => r.ProductId == product && r.OverallStatus == ComplianceStatus.ReadyToPublishSubjectToReview).OrderByDescending(r => r.Id).FirstAsync());
        Assert.Equal(Submitted + " Machine washable.", review.FinalDescription);
    }

    // who submits what

    [Fact]
    public async Task ASeniorEditorsOwnText_WinsOverTheirDraft_WhenTheySendSome()
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(editor, "Editor's own product");
        await editor.PutAsJsonAsync($"/api/products/{product}/draft", new { text = "An old draft the editor forgot about.", market = "UK" });

        var body = await (await SubmitAsync(editor, product, Submitted)).Content.ReadFromJsonAsync<MarkReadyResponse>();

        Assert.True(body!.ReadyToPublishSubjectToReview);
        Assert.False(body.UsedDraft);
        var review = await _factory.WithDbAsync(db => db.ComplianceReviews.SingleAsync(r => r.ProductId == product));
        Assert.Equal(Submitted, review.FinalDescription);
    }

    [Fact]
    public async Task ASeniorEditorWhoSendsNoText_SubmitsTheirDraft()
    {
        var editor = _factory.CreateSeniorEditor(NewUser());
        var product = await CreateProductAsync(editor, "Editor's draft product");
        await editor.PutAsJsonAsync($"/api/products/{product}/draft", new { text = Submitted, market = "UK" });

        var body = await (await SubmitAsync(editor, product, text: null)).Content.ReadFromJsonAsync<MarkReadyResponse>();

        Assert.True(body!.ReadyToPublishSubjectToReview);
        Assert.True(body.UsedDraft);
    }
}
