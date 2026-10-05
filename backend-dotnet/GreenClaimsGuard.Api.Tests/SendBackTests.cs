using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Endpoints;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// send back pulls it out of the queue and adds a new history entry, writer sees why and can resubmit
[Collection(ApiCollection.Name)]
public class SendBackTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string Comment = "The organic claim has no certificate behind it.";

    private readonly ApiTestFactory _factory;

    public SendBackTests(ApiTestFactory factory)
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

    private static Task<HttpResponseMessage> SendBackAsync(HttpClient client, Guid product, string? reason = "not_accurate", string? comment = Comment) =>
        client.PostAsJsonAsync("/api/claims/send-back", new { productId = product, reasonCategory = reason, comment });

    private static async Task<IReadOnlyList<Guid>> QueueAsync(HttpClient editor) =>
        (await editor.GetFromJsonAsync<JsonElement>("/api/claims/pending-review")).EnumerateArray().Select(i => i.GetProperty("productId").GetGuid()).ToList();

    [Fact]
    public async Task SendBack_RoundTrip_TheWriterSeesWhy_EditsAndSubmitsAgain()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var product = await CreateProductAsync(writer, "Round trip product");
        await SubmitAsync(writer, product);
        Assert.Contains(product, await QueueAsync(editor));

        var sent = await SendBackAsync(editor, product, "not_substantiated", Comment);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        Assert.Equal("SentBack", (await sent.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // it's out of the queue now and can't be published while it's with the writer
        Assert.DoesNotContain(product, await QueueAsync(editor));
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostAsJsonAsync("/api/claims/publish", new { productId = product })).StatusCode);

        // writer can see the reason and comment in My submissions, and on the product too
        var row = (await writer.GetFromJsonAsync<JsonElement>("/api/my/submissions")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal("SentBack", row.GetProperty("status").GetString());
        var sendBack = row.GetProperty("sendBack");
        Assert.Equal("not_substantiated", sendBack.GetProperty("reasonCategory").GetString());
        Assert.Equal("Not substantiated", sendBack.GetProperty("reasonLabel").GetString());
        Assert.Equal(Comment, sendBack.GetProperty("comment").GetString());

        var opened = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}");
        Assert.Equal("SentBack", opened.GetProperty("status").GetString());
        Assert.Equal(Comment, opened.GetProperty("sendBack").GetProperty("comment").GetString());

        // copy's editable again, but you can't withdraw something that's already been sent back
        Assert.Equal(HttpStatusCode.OK, (await writer.PatchAsJsonAsync($"/api/products/{product}", new { name = "Round trip revised" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await writer.PutAsJsonAsync($"/api/products/{product}/draft", new { text = CleanText + " Machine washable.", market = "UK" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = product })).StatusCode);

        // resubmitting puts it back in the queue and clears the send-back off the current state
        Assert.True((await SubmitAsync(writer, product)).IsSuccessStatusCode);
        Assert.Contains(product, await QueueAsync(editor));
        var again = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}");
        Assert.Equal("InReview", again.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("sendBack").ValueKind);

        // history keeps both versions and remembers why the first one got sent back
        var history = await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}/history");
        var versions = history.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(2, versions.Count);
        Assert.Equal("Not substantiated", versions[0].GetProperty("sendBack").GetProperty("reasonLabel").GetString());
        Assert.Equal(JsonValueKind.Null, versions[1].GetProperty("sendBack").ValueKind);
    }

    [Theory]
    [InlineData("not_accurate", "Not accurate")]
    [InlineData("vague_or_unclear", "Vague or unclear")]
    [InlineData("missing_information", "Missing information")]
    [InlineData("unfair_comparison", "Unfair comparison")]
    [InlineData("not_full_life_cycle", "Not full life cycle")]
    [InlineData("not_substantiated", "Not substantiated")]
    [InlineData("NOT_ACCURATE", "Not accurate")]
    public async Task EachOfTheSixReasons_IsAccepted_AndShownByItsLabel(string code, string label)
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, $"Reason {code}");
        await SubmitAsync(writer, product);

        Assert.Equal(HttpStatusCode.OK, (await SendBackAsync(_factory.CreateSeniorEditor(), product, code)).StatusCode);

        var row = (await writer.GetFromJsonAsync<JsonElement>("/api/my/submissions")).EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal(label, row.GetProperty("sendBack").GetProperty("reasonLabel").GetString());
    }

    [Theory]
    [InlineData(null, Comment)]
    [InlineData("", Comment)]
    [InlineData("because_i_said_so", Comment)]
    [InlineData("not_accurate", null)]
    [InlineData("not_accurate", "   ")]
    [InlineData("not_accurate", "too short")]
    public async Task ASendBack_NeedsARealReasonAndARealComment(string? reason, string? comment)
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Invalid send back");
        await SubmitAsync(writer, product);

        var response = await SendBackAsync(_factory.CreateSeniorEditor(), product, reason, comment);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(product, await QueueAsync(_factory.CreateSeniorEditor()));
    }

    [Fact]
    public async Task ATooLongComment_IsRefused()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Long comment");
        await SubmitAsync(writer, product);

        var response = await SendBackAsync(_factory.CreateSeniorEditor(), product, "not_accurate", new string('c', SendBackEndpoints.MaxCommentLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OnlyASeniorEditor_CanSendBack()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Not for writers");
        await SubmitAsync(writer, product);

        Assert.Equal(HttpStatusCode.Forbidden, (await SendBackAsync(writer, product)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendBackAsync(_factory.CreateCopywriter(NewUser()), product)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendBackAsync(_factory.CreateAnonymousClient(), product)).StatusCode);
        Assert.Equal("InReview", (await writer.GetFromJsonAsync<JsonElement>($"/api/products/{product}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ThereMustBeSomethingWaitingToSendBack()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor();
        var draft = await CreateProductAsync(writer, "Never submitted");
        var live = await CreateProductAsync(writer, "Already live");
        await SubmitAsync(writer, live);
        await editor.PostAsJsonAsync("/api/claims/publish", new { productId = live });
        var twice = await CreateProductAsync(writer, "Sent back twice");
        await SubmitAsync(writer, twice);
        await SendBackAsync(editor, twice);
        var withdrawn = await CreateProductAsync(writer, "Withdrawn first");
        await SubmitAsync(writer, withdrawn);
        await writer.PostAsJsonAsync("/api/claims/withdraw", new { productId = withdrawn });

        Assert.Equal(HttpStatusCode.Conflict, (await SendBackAsync(editor, draft)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendBackAsync(editor, live)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendBackAsync(editor, twice)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendBackAsync(editor, withdrawn)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendBackAsync(editor, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task ASendBack_IsAppendedToTheHistoryAndTheAuditTrail_WithWhoWhyAndWhat()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var editor = _factory.CreateSeniorEditor("auth0|sending-editor-9");
        var product = await CreateProductAsync(writer, "Audited send back");
        await SubmitAsync(writer, product);
        await SendBackAsync(editor, product, "vague_or_unclear", "Say which part of the shirt is recycled.");

        var steps = await _factory.WithDbAsync(db => db.ComplianceReviews.Where(r => r.ProductId == product).OrderBy(r => r.Id).ToListAsync());
        Assert.Equal(new[] { ComplianceStatus.ReadyToPublishSubjectToReview, ComplianceStatus.SentBack }, steps.Select(s => s.OverallStatus));
        Assert.Equal("auth0|sending-editor-9", steps[1].ActorUserId);
        Assert.Equal("vague_or_unclear", steps[1].SendBackReason);
        Assert.Equal("Say which part of the shirt is recycled.", steps[1].SendBackComment);
        Assert.Equal(CleanText, steps[1].FinalDescription);

        var entries = await _factory.WithDbAsync(db => db.AuditLedger.Where(e => e.ProductId == product).ToListAsync());
        var row = Assert.Single(entries, e => e.Action == AuditActions.SendBack);
        Assert.Equal("auth0|sending-editor-9", row.UserId);
        Assert.Equal("Say which part of the shirt is recycled.", row.Justification);
        Assert.Equal("Sent back: Vague or unclear", row.Detail);
        Assert.Equal(CleanText, row.CopySnapshot);
        Assert.Contains(entries, e => e.Action == AuditActions.Submit);
    }

    [Fact]
    public async Task ASentBackProduct_IsStillPrivateToItsWriter()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Private send back");
        await SubmitAsync(writer, product);
        await SendBackAsync(_factory.CreateSeniorEditor(), product);

        var other = _factory.CreateCopywriter(NewUser());
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/products/{product}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/products/{product}/history")).StatusCode);
        Assert.DoesNotContain(product, (await other.GetFromJsonAsync<JsonElement>("/api/my/submissions")).EnumerateArray().Select(r => r.GetProperty("productId").GetGuid()));
    }

    [Fact]
    public async Task AWriterStatusAppearsInTheProductList()
    {
        var writer = _factory.CreateCopywriter(NewUser());
        var product = await CreateProductAsync(writer, "Listed after send back");
        await SubmitAsync(writer, product);
        await SendBackAsync(_factory.CreateSeniorEditor(), product);

        var listed = (await writer.GetFromJsonAsync<JsonElement>("/api/products")).EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == product);
        Assert.Equal("SentBack", listed.GetProperty("status").GetString());
    }
}
