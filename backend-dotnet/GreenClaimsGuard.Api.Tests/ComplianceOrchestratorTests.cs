using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GreenClaimsGuard.Api.Tests;

// tests the orchestrator by itself, with fake ai and db we control
[Collection(ApiCollection.Name)]
public class ComplianceOrchestratorTests
{
    private const string CleanText = "A plain cotton shirt with a relaxed fit.";
    private const string CriticalText = "Our new range is made from sustainable bamboo fabric.";

    private readonly ApiTestFactory _factory;

    public ComplianceOrchestratorTests(ApiTestFactory factory)
    {
        _factory = factory;
    }

    private ComplianceOrchestrator Build(ILlmService llm, IDbService db) => new(
        _factory.Services.GetRequiredService<IRuleEngineService>(),
        llm,
        _factory.Services.GetRequiredService<IAggregationService>(),
        db,
        _factory.Services.GetRequiredService<IProductFactsProvider>(),
        NullLogger<ComplianceOrchestrator>.Instance);

    [Fact]
    public async Task WithNoOpenAiKey_TheRulesStillRun_AndTheAiIsReportedAsNotConfigured()
    {
        var noKeyConfig = new ConfigurationBuilder().Build();
        var noKeyLlm = new LlmService(
            noKeyConfig,
            new OpenAiComplianceClient(noKeyConfig, NullLogger<OpenAiComplianceClient>.Instance),
            NullLogger<LlmService>.Instance);
        var orchestrator = Build(noKeyLlm, new FakeDbService());

        var result = await orchestrator.EvaluateAsync(new ComplianceRequest
        {
            Text = CriticalText,
            Action = ComplianceAction.Analyze,
            RulesOnly = false
        });

        Assert.Equal(EngineStatus.Ok, result.RulesStatus);
        Assert.Equal(EngineStatus.NotConfigured, result.AiStatus);
        Assert.Contains(result.RuleFindings, f => f.RuleId == "FASH-2");
        Assert.False(result.PublishAllowed);
    }

    [Fact]
    public async Task ARulesOnlyCheck_SkipsTheAi_ButSubmitAndPublishNeverDo()
    {
        var fake = new FakeLlmService();
        var orchestrator = Build(fake, new FakeDbService());

        var live = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, RulesOnly = true });
        Assert.Equal(EngineStatus.Skipped, live.AiStatus);

        // rulesOnly only matters for a live check
        var submit = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, Action = ComplianceAction.Submit, RulesOnly = true });
        var publish = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, Action = ComplianceAction.Publish, RulesOnly = true });
        Assert.Equal(EngineStatus.Ok, submit.AiStatus);
        Assert.Equal(EngineStatus.Ok, publish.AiStatus);
    }

    [Fact]
    public async Task WhenTheDatabaseIsUnavailable_PublishIsBlocked_ButSubmitIsNot()
    {
        var orchestrator = Build(new FakeLlmService(), new FakeDbService { Available = false });

        var submit = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, Action = ComplianceAction.Submit });
        var publish = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, Action = ComplianceAction.Publish });

        Assert.Equal(EngineStatus.Unavailable, publish.DbStatus);
        Assert.True(submit.SubmitAllowed);
        Assert.False(publish.PublishAllowed);
        Assert.Contains(publish.BlockingReasons, r => r.Contains("database is unavailable"));
    }

    [Fact]
    public async Task WhenTheAiFails_PublishIsBlocked_ButSubmitIsNot()
    {
        var orchestrator = Build(new FakeLlmService { Status = EngineStatus.Failed }, new FakeDbService());

        var result = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, Action = ComplianceAction.Publish });

        Assert.Equal(EngineStatus.Failed, result.AiStatus);
        Assert.True(result.SubmitAllowed);
        Assert.False(result.PublishAllowed);
        Assert.Contains(result.BlockingReasons, r => r.Contains("AI check failed"));
    }

    [Fact]
    public async Task ACleanCheck_IsAllowedToSubmitAndPublish_WhenEverythingIsHealthy()
    {
        var orchestrator = Build(new FakeLlmService(), new FakeDbService());

        var result = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = CleanText, Action = ComplianceAction.Publish });

        Assert.True(result.SubmitAllowed);
        Assert.True(result.PublishAllowed);
        Assert.Empty(result.BlockingReasons);
        Assert.Equal(0, result.OpenIssueCount);
    }

    [Fact]
    public async Task AnEmptyDescription_IsNeverAllowed()
    {
        var orchestrator = Build(new FakeLlmService(), new FakeDbService());

        var result = await orchestrator.EvaluateAsync(new ComplianceRequest { Text = "   ", Action = ComplianceAction.Submit });

        Assert.False(result.SubmitAllowed);
        Assert.Contains(result.SubmitBlockingReasons, r => r.Contains("empty"));
    }

    [Fact]
    public void TheRulesVersion_IdentifiesTheRuleSet()
    {
        var rules = _factory.Services.GetRequiredService<IRuleEngineService>();

        Assert.Matches("^UK-[0-9a-f]{12}$", rules.RulesVersionFor(Markets.Uk));
        Assert.Matches("^EU-[0-9a-f]{12}$", rules.RulesVersionFor(Markets.Eu));
        Assert.NotEqual(rules.RulesVersionFor(Markets.Uk)[3..], rules.RulesVersionFor(Markets.Eu)[3..]);
    }
}
