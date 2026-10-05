using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using GreenClaimsGuard.Api.Security;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// both the server and the screen work out someone's persona (Copywriter or Senior Editor) from the permissions on their token, no permissions means no access
public class PersonaUnitTests
{
    private static ClaimsPrincipal With(params string[] permissions) =>
        new(new ClaimsIdentity(
            permissions.Select(p => new Claim(AuthorizationSetup.PermissionsClaimType, p)),
            authenticationType: "Test"));

    [Fact]
    public void AnalysePermission_MakesACopywriter()
    {
        Assert.Equal(Personas.Copywriter, Personas.Of(With("analyse:claims")));
    }

    [Fact]
    public void PublishPermission_MakesASeniorEditor_WhetherOrNotAnalyseIsAlsoHeld()
    {
        Assert.Equal(Personas.SeniorEditor, Personas.Of(With("analyse:claims", "publish:product")));
        Assert.Equal(Personas.SeniorEditor, Personas.Of(With("publish:product")));
    }

    [Theory]
    [InlineData("resolve:findings")]
    [InlineData("something:else")]
    public void AnyOtherPermission_IsNoPersonaAtAll(string permission)
    {
        Assert.Equal(Personas.None, Personas.Of(With(permission)));
        Assert.Equal(Personas.None, Personas.Of(With()));
        Assert.Equal(Personas.None, Personas.Of(null));
    }

    [Fact]
    public void ARoleNameIsNotAPersona_OnlyPermissionsAre()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "compliance-manager") }, "Test"));
        Assert.Equal(Personas.None, Personas.Of(principal));
    }

    [Theory]
    [InlineData(Personas.Copywriter, "Copywriter")]
    [InlineData(Personas.SeniorEditor, "Senior Editor")]
    [InlineData(Personas.None, "No access")]
    public void EachPersonaHasOneLabel(string persona, string label)
    {
        Assert.Equal(label, Personas.LabelOf(persona));
    }
}

[Collection(ApiCollection.Name)]
public class PersonaEndpointTests
{
    private readonly ApiTestFactory _factory;

    public PersonaEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Me_NamesACopywriter_AndSaysTheyCannotPublish()
    {
        var me = await _factory.CreateCopywriter("auth0|cw-1").GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal("copywriter", me.GetProperty("persona").GetString());
        Assert.Equal("Copywriter", me.GetProperty("label").GetString());
        Assert.False(me.GetProperty("canPublish").GetBoolean());
        Assert.Equal("auth0|cw-1", me.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task Me_NamesASeniorEditor_AndSaysTheyCanPublish()
    {
        var me = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/me");

        Assert.Equal("senior-editor", me.GetProperty("persona").GetString());
        Assert.Equal("Senior Editor", me.GetProperty("label").GetString());
        Assert.True(me.GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public async Task Me_IsRefusedToAnonymous()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateAnonymousClient().GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task SomeoneSignedInWithNoRole_IsToldSoByMe_ButCanDoNothingElse()
    {
        var nobody = _factory.CreateClientAs($"nobody-{Guid.NewGuid():N}");

        var me = await nobody.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.Equal("none", me.GetProperty("persona").GetString());
        Assert.Equal("No access", me.GetProperty("label").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsJsonAsync("/api/analyze", new { text = "A plain cotton shirt.", rulesOnly = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsJsonAsync("/api/products", new { name = "Sneaky" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsJsonAsync("/api/claims/mark-ready", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/dashboard/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/claims/pending-review")).StatusCode);
    }

    [Fact]
    public async Task AnOldLeftoverPermission_GrantsNothing()
    {
        var leftover = _factory.CreateClientAs($"leftover-{Guid.NewGuid():N}", "resolve:findings");

        Assert.Equal(HttpStatusCode.Forbidden, (await leftover.PostAsJsonAsync("/api/analyze", new { text = "A plain cotton shirt.", rulesOnly = true })).StatusCode);
    }

    [Fact]
    public async Task HealthStaysPublic()
    {
        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateAnonymousClient().GetAsync("/health")).StatusCode);
    }
}
