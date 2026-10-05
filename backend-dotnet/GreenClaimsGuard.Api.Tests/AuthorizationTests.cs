using System.Net;
using System.Net.Http.Json;
using GreenClaimsGuard.Api.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthorizationTests
{
    private readonly ApiTestFactory _factory;

    public AuthorizationTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_IsPublic()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/claims/recent")]
    [InlineData("GET", "/api/claims/pending-review")]
    [InlineData("POST", "/api/analyze")]
    [InlineData("POST", "/api/products")]
    [InlineData("POST", "/api/claims/mark-ready")]
    [InlineData("POST", "/api/claims/publish")]
    [InlineData("POST", "/api/data-transfer/seed")]
    [InlineData("POST", "/api/claims/import-public-seed")]
    [InlineData("GET", "/api/status")]
    [InlineData("GET", "/api/dashboard/overview")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/audit/export")]
    [InlineData("GET", "/api/products/00000000-0000-0000-0000-000000000001/facts")]
    [InlineData("PUT", "/api/products/00000000-0000-0000-0000-000000000001/facts")]
    [InlineData("PATCH", "/api/products/00000000-0000-0000-0000-000000000001")]
    public async Task Anonymous_GetsUnauthorized_OnEveryProtectedEndpoint(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await _factory.CreateAnonymousClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/claims/publish")]
    [InlineData("POST", "/api/data-transfer/seed")]
    [InlineData("POST", "/api/claims/import-public-seed")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/audit/export")]
    [InlineData("PUT", "/api/products/00000000-0000-0000-0000-000000000001/facts")]
    public async Task Copywriter_IsForbidden_FromPublishLevelEndpoints(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT" or "PATCH")
        {
            request.Content = JsonContent.Create(new { });
        }

        var response = await _factory.CreateCopywriter().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SeniorEditor_CanReachPublishLevelEndpoints()
    {
        var response = await _factory.CreateSeniorEditor().GetAsync("/api/claims/pending-review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnyLoggedInUser_CanSubmitButOnlySeniorEditorCanPublish()
    {
        var copywriter = _factory.CreateCopywriter();
        var seniorEditor = _factory.CreateSeniorEditor();

        var created = await copywriter.PostAsJsonAsync("/api/products", new { name = "Authorization test product" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var productId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();

        var submit = await copywriter.PostAsJsonAsync("/api/claims/mark-ready", new
        {
            productId,
            finalDescription = "A plain cotton shirt.",
            issueDecisions = Array.Empty<object>()
        });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);

        var copywriterPublish = await copywriter.PostAsJsonAsync("/api/claims/publish", new { productId });
        Assert.Equal(HttpStatusCode.Forbidden, copywriterPublish.StatusCode);

        var seniorEditorPublish = await seniorEditor.PostAsJsonAsync("/api/claims/publish", new { productId });
        Assert.Equal(HttpStatusCode.OK, seniorEditorPublish.StatusCode);
    }

    [Fact]
    public async Task FallbackPolicy_ClosesAnEndpointThatHasNoPolicyOfItsOwn()
    {
        // a tiny host using our real auth setup, with one endpoint that (by mistake) has no policy attached. it should still need a signed in user
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddGreenClaimsAuthorization();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/forgot-a-policy", () => "secret");
        app.MapGet("/open", () => "ok").AllowAnonymous();
        await app.StartAsync();

        var anonymous = app.GetTestClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/forgot-a-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/open")).StatusCode);

        // signed in but no role assigned, still gets refused. just being logged in isn't enough
        var noRole = app.GetTestClient();
        noRole.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "someone");
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync("/forgot-a-policy")).StatusCode);

        var copywriter = app.GetTestClient();
        copywriter.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "someone");
        copywriter.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, AuthorizationSetup.AnalysePermission);
        Assert.Equal(HttpStatusCode.OK, (await copywriter.GetAsync("/forgot-a-policy")).StatusCode);
    }
}
