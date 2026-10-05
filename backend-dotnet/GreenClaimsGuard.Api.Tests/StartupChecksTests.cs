using GreenClaimsGuard.Api.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// a bad setting should stop the app with a clear message, not let it run looking fine when it's not
public class StartupChecksTests
{
    private static IHostEnvironment Env(string name) => new FakeEnvironment(name);

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData(null, "https://greenclaims-api")]
    [InlineData("", "https://greenclaims-api")]
    [InlineData("tenant.auth0.com", null)]
    [InlineData("tenant.auth0.com", "  ")]
    [InlineData(null, null)]
    public void Production_RefusesToStart_WithoutBothAuth0Settings(string? domain, string? audience)
    {
        var error = Assert.Throws<InvalidOperationException>(() => StartupChecks.EnsureSafeToStart(Env("Production"), domain, audience));

        Assert.Contains("Refusing to start in Production without Auth0", error.Message);
        Assert.Contains("AUTH0_DOMAIN", error.Message);
    }

    [Fact]
    public void Production_StartsFine_WithAuth0Set()
    {
        StartupChecks.EnsureSafeToStart(Env("Production"), "tenant.auth0.com", "https://greenclaims-api");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public void OtherEnvironments_MayStartWithoutAuth0(string environment)
    {
        StartupChecks.EnsureSafeToStart(Env(environment), null, null);
    }
}
