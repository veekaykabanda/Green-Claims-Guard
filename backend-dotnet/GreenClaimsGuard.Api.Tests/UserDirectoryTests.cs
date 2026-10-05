using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using GreenClaimsGuard.Api.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// queue and audit trail show the email from the login token, but it's just for display, never used to decide what you can do
[Collection(ApiCollection.Name)]
public class UserDirectoryTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";

    private readonly ApiTestFactory _factory;

    public UserDirectoryTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private static string NewUser() => $"auth0|writer-{Guid.NewGuid():N}";

    private HttpClient WriterWithEmail(string userId, string? email)
    {
        var client = _factory.CreateCopywriter(userId);
        if (email is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, email);
        return client;
    }

    private static async Task<Guid> SubmitProductAsync(HttpClient writer, string name)
    {
        var created = await writer.PostAsJsonAsync("/api/products", new { name });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var saved = await writer.PutAsJsonAsync($"/api/products/{id}/draft", new { text = CleanText, market = "UK" });
        saved.EnsureSuccessStatusCode();
        var submitted = await writer.PostAsJsonAsync("/api/claims/mark-ready", new { productId = id, finalDescription = CleanText, issueDecisions = Array.Empty<object>() });
        submitted.EnsureSuccessStatusCode();
        Assert.True((await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("readyToPublishSubjectToReview").GetBoolean(), "the submission was refused");
        return id;
    }

    [Theory]
    [InlineData("ada@brand.com", "ada@brand.com")]
    [InlineData("  ada@brand.com  ", "ada@brand.com")]
    [InlineData("not-an-email", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyAnEmailShapedClaimIsUsed(string? claim, string? expected)
    {
        var claims = new List<Claim>();
        if (claim is not null) claims.Add(new Claim(CurrentUser.EmailClaimType, claim));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        Assert.Equal(expected, CurrentUser.GetEmail(user));
    }

    [Fact]
    public void AnEmailTooLongToStoreIsIgnored()
    {
        var tooLong = new string('a', 320) + "@brand.com";
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(CurrentUser.EmailClaimType, tooLong) }, "test"));

        Assert.Null(CurrentUser.GetEmail(user));
    }

    [Fact]
    public async Task ASignedInUsersEmailIsRemembered_AndShownToTheSeniorEditorInTheQueue()
    {
        var userId = NewUser();
        var writer = WriterWithEmail(userId, "ada@brand.com");
        var product = await SubmitProductAsync(writer, "Linen shirt");

        var stored = await _factory.WithDbAsync(db => db.UserDirectory.SingleAsync(u => u.UserId == userId));
        Assert.Equal("ada@brand.com", stored.Email);

        var queue = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/claims/pending-review");
        var row = queue.EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal(userId, row.GetProperty("submittedByUserId").GetString());
        Assert.Equal("ada@brand.com", row.GetProperty("submittedByEmail").GetString());
    }

    [Fact]
    public async Task WithNoEmailOnTheToken_NothingIsStored_AndTheQueueStillShowsTheId()
    {
        var userId = NewUser();
        var writer = WriterWithEmail(userId, null);
        var product = await SubmitProductAsync(writer, "Plain cotton shirt");

        Assert.False(await _factory.WithDbAsync(db => db.UserDirectory.AnyAsync(u => u.UserId == userId)));

        var queue = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/claims/pending-review");
        var row = queue.EnumerateArray().Single(r => r.GetProperty("productId").GetGuid() == product);
        Assert.Equal(userId, row.GetProperty("submittedByUserId").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("submittedByEmail").ValueKind);
    }

    [Fact]
    public async Task AChangedEmailUpdatesTheSameRow_InsteadOfAddingAnother()
    {
        var userId = NewUser();
        (await WriterWithEmail(userId, "old@brand.com").GetAsync("/api/me")).EnsureSuccessStatusCode();
        (await WriterWithEmail(userId, "new@brand.com").GetAsync("/api/me")).EnsureSuccessStatusCode();

        var rows = await _factory.WithDbAsync(db => db.UserDirectory.Where(u => u.UserId == userId).ToListAsync());
        Assert.Equal("new@brand.com", Assert.Single(rows).Email);
    }

    [Fact]
    public async Task TheAuditTrailNamesTheUser_ButStillRecordsTheId()
    {
        var userId = NewUser();
        var writer = WriterWithEmail(userId, "ada@brand.com");
        var product = await SubmitProductAsync(writer, "Wool jumper");

        var audit = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>($"/api/audit?productId={product}");
        var submit = audit.EnumerateArray().First(e => e.GetProperty("action").GetString() == "Submit");
        Assert.Equal(userId, submit.GetProperty("userId").GetString());
        Assert.Equal("ada@brand.com", submit.GetProperty("userEmail").GetString());

        var csv = await _factory.CreateSeniorEditor().GetStringAsync($"/api/audit/export?productId={product}");
        Assert.Contains("\"User email\"", csv);
        Assert.Contains("\"ada@brand.com\"", csv);
    }

    [Fact]
    public async Task AnEmailNeverChangesWhatSomeoneMayDo()
    {
        // A writer whose token claims a Senior Editor's email is still just a writer.
        var writer = WriterWithEmail(NewUser(), "boss@brand.com");
        var response = await writer.GetAsync("/api/audit");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }
}
