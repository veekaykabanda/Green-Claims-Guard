using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

public class AiHealthTrackerTests
{
    [Fact]
    public void WithNoKey_TheAiIsReportedAsNotConfigured_NoMatterWhatWasRecorded()
    {
        var tracker = new AiHealthTracker { Configured = false };
        tracker.Record(EngineStatus.Ok);

        Assert.Equal(EngineStatus.NotConfigured, tracker.Snapshot().Status);
    }

    [Fact]
    public void ANewlyConfiguredAi_IsOk_UntilACallSaysOtherwise()
    {
        var tracker = new AiHealthTracker { Configured = true };
        Assert.Equal(EngineStatus.Ok, tracker.Snapshot().Status);
        Assert.Null(tracker.Snapshot().LastCheckedAt);

        tracker.Record(EngineStatus.Timeout);
        var (status, at) = tracker.Snapshot();
        Assert.Equal(EngineStatus.Timeout, status);
        Assert.NotNull(at);

        tracker.Record(EngineStatus.Ok);
        Assert.Equal(EngineStatus.Ok, tracker.Snapshot().Status);
    }
}

[Collection(ApiCollection.Name)]
public class StatusEndpointTests
{
    private readonly ApiTestFactory _factory;

    public StatusEndpointTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var response = await _factory.CreateAnonymousClient().GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASeniorEditor_SeesRulesAiAndDatabase()
    {
        var status = await _factory.CreateSeniorEditor().GetFromJsonAsync<JsonElement>("/api/status");

        var rules = status.GetProperty("rules");
        Assert.Equal(EngineStatus.Ok, rules.GetProperty("status").GetString());
        Assert.StartsWith("UK-", rules.GetProperty("ukVersion").GetString());
        Assert.StartsWith("EU-", rules.GetProperty("euVersion").GetString());
        Assert.True(rules.GetProperty("ukRuleCount").GetInt32() > 0);
        Assert.True(rules.GetProperty("euRuleCount").GetInt32() > 0);

        Assert.Equal(EngineStatus.Ok, status.GetProperty("database").GetProperty("status").GetString());
        Assert.Contains(status.GetProperty("ai").GetProperty("status").GetString(), new[] { EngineStatus.Ok, EngineStatus.NotConfigured });
    }
}
