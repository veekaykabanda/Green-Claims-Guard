using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Security;
using GreenClaimsGuard.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GreenClaimsGuard.Api.Tests;

// one factory shared by every api test. runs the real app against a throwaway sql server db built by our own migrations. defaults to localdb, or set TEST_SQL_SERVER where localdb isn't available (ci, linux)
public class ApiTestFactory : WebApplicationFactory<Program>
{
    public const string SeniorEditorPermission = AuthorizationSetup.PublishPermission;
    public const string OverridePermission = AuthorizationSetup.OverridePermission;
    public const string EditFactsPermission = AuthorizationSetup.EditFactsPermission;

    private const string ConnectionVariable = "SQL_SERVER_CONNECTION";
    private const string TestServerVariable = "TEST_SQL_SERVER";
    private readonly string _databaseName = $"gcg_test_{Guid.NewGuid():N}";
    private readonly string? _previousConnection;

    public ApiTestFactory()
    {
        // app reads its connection string from the environment before any test hooks run
        _previousConnection = Environment.GetEnvironmentVariable(ConnectionVariable);
        var testServer = Environment.GetEnvironmentVariable(TestServerVariable);
        Environment.SetEnvironmentVariable(
            ConnectionVariable,
            string.IsNullOrWhiteSpace(testServer)
                ? $"Server=(localdb)\\mssqllocaldb;Database={_databaseName};Trusted_Connection=True;TrustServerCertificate=True;"
                : $"{testServer.Trim().TrimEnd(';')};Database={_databaseName};TrustServerCertificate=True;");
    }

    public FakeLlmService Llm { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILlmService>();
            services.AddSingleton<ILlmService>(Llm);

            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    public HttpClient CreateAnonymousClient() => CreateClient();

    public HttpClient CreateClientAs(string userId, params string[] permissions)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, string.Join(",", permissions));
        }
        return client;
    }

    // each client is its own user unless a test names one, so tests don't share a rate limit
    public HttpClient CreateCopywriter(string? userId = null) =>
        CreateClientAs(userId ?? $"copywriter-{Guid.NewGuid():N}", "analyse:claims");

    public HttpClient CreateSeniorEditor(string? userId = null) =>
        CreateClientAs(userId ?? $"editor-{Guid.NewGuid():N}", "analyse:claims", SeniorEditorPermission, OverridePermission, EditFactsPermission);

    // a senior editor without the two extra permissions, can sign off but can't override or edit facts
    public HttpClient CreateBasicSeniorEditor(string? userId = null) =>
        CreateClientAs(userId ?? $"editor-{Guid.NewGuid():N}", "analyse:claims", SeniorEditorPermission);

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                using var scope = Services.CreateScope();
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeleted();
            }
            catch
            {
                // best effort, a leftover throwaway db doesn't hurt anything
            }

            Environment.SetEnvironmentVariable(ConnectionVariable, _previousConnection);
        }

        base.Dispose(disposing);
    }
}

[CollectionDefinition(Name)]
public class ApiCollection : ICollectionFixture<ApiTestFactory>
{
    public const string Name = "Api";
}
