using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace GreenClaimsGuard.Api.Services;

public class OpenAiComplianceClient : IAiComplianceClient
{
    // total time allowed for one check, retries included. 5s was cutting off every real call before it finished (timed it myself), a strict JSON reply this size takes about 9-10s from OpenAI
    public const int DefaultTimeoutSeconds = 20;

    // one retry for a dropped connection or a 429/5xx, runs inside the same deadline so it can't push past the timeout
    private const int MaxRetries = 1;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ChatClient? _chat;
    private readonly TimeSpan _timeout;
    private readonly ILogger<OpenAiComplianceClient> _logger;
    private readonly AiHealthTracker _health;

    public OpenAiComplianceClient(IConfiguration config, ILogger<OpenAiComplianceClient> logger, AiHealthTracker? health = null)
    {
        _logger = logger;
        _health = health ?? new AiHealthTracker();
        _timeout = TimeSpan.FromSeconds(config.GetValue<int?>("OpenAI:TimeoutSeconds") ?? DefaultTimeoutSeconds);

        var apiKey = config["OpenAI:ApiKey"] ?? config["OpenAI__ApiKey"] ?? config["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("OpenAI API key not configured; AI compliance checks will be unavailable");
            return;
        }

        var options = new OpenAIClientOptions
        {
            NetworkTimeout = _timeout,
            RetryPolicy = new ClientRetryPolicy(MaxRetries)
        };
        _health.Configured = true;
        _chat = new ChatClient(config["OpenAI:Model"] ?? "gpt-4o-mini", new ApiKeyCredential(apiKey), options);
        _logger.LogInformation("OpenAI compliance client ready (timeout {Timeout}s, {Retries} retry)", _timeout.TotalSeconds, MaxRetries);
    }

    public async Task<AiCheckResult> CheckAsync(AiCheckRequest request, CancellationToken cancellationToken)
    {
        var result = await CheckCoreAsync(request, cancellationToken);
        if (result.Status != EngineStatusNames.NotConfigured) _health.Record(result.Status);
        return result;
    }

    private async Task<AiCheckResult> CheckCoreAsync(AiCheckRequest request, CancellationToken cancellationToken)
    {
        if (_chat is null)
        {
            return new AiCheckResult { Status = EngineStatusNames.NotConfigured };
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(CompliancePrompts.System(request.Industry, request.Market)),
                new UserChatMessage(CompliancePrompts.User(request))
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 1800,
                Temperature = 0.2f,
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "green_claims_check",
                    jsonSchema: BinaryData.FromString(CompliancePrompts.Schema),
                    jsonSchemaIsStrict: true)
            };

            var completion = await _chat.CompleteChatAsync(messages, options, deadline.Token);

            if (completion.Value.FinishReason != ChatFinishReason.Stop || completion.Value.Content.Count == 0)
            {
                return Failed($"The model did not finish cleanly (finish reason: {completion.Value.FinishReason}).");
            }

            var parsed = JsonSerializer.Deserialize<ModelReply>(completion.Value.Content[0].Text, JsonOptions);
            if (parsed is null)
            {
                return Failed("The model returned nothing that could be read.");
            }

            return new AiCheckResult
            {
                Status = EngineStatusNames.Ok,
                RiskLevel = parsed.RiskLevel ?? "none",
                Summary = parsed.Summary ?? "",
                CompliantRewrite = parsed.CompliantRewrite ?? "",
                Violations = (parsed.DetectedViolations ?? new()).Select(v => new AiCheckViolation
                {
                    Phrase = v.Phrase ?? "",
                    RuleViolated = v.RuleViolated ?? "",
                    Severity = v.Severity ?? "WARNING",
                    Replacement = v.Replacement ?? ""
                }).ToList(),
                SentenceRewrites = (parsed.SentenceRewrites ?? new()).Select(r => new AiSentenceRewrite
                {
                    SentenceId = r.SentenceId,
                    Rewrite = r.Rewrite ?? ""
                }).ToList()
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("AI compliance check timed out after {Seconds}s", _timeout.TotalSeconds);
            return new AiCheckResult { Status = EngineStatusNames.Timeout };
        }
        catch (JsonException ex)
        {
            // A reply that does not match the schema is treated as if the AI were unavailable.
            _logger.LogError(ex, "AI compliance reply did not match the schema");
            return Failed("The reply did not match the expected schema.");
        }
        catch (Exception ex) when (ex is ClientResultException or HttpRequestException or InvalidOperationException)
        {
            _logger.LogError(ex, "AI compliance check failed");
            return Failed(ex.Message);
        }
    }

    private AiCheckResult Failed(string reason)
    {
        _logger.LogWarning("AI compliance check failed: {Reason}", reason);
        return new AiCheckResult { Status = EngineStatusNames.Failed };
    }

    private sealed class ModelReply
    {
        public string? RiskLevel { get; set; }
        public string? Summary { get; set; }
        public string? CompliantRewrite { get; set; }
        public List<ModelViolation>? DetectedViolations { get; set; }
        public List<ModelRewrite>? SentenceRewrites { get; set; }
    }

    private sealed class ModelViolation
    {
        public string? Phrase { get; set; }
        public string? RuleViolated { get; set; }
        public string? Severity { get; set; }
        public string? Replacement { get; set; }
    }

    private sealed class ModelRewrite
    {
        public int SentenceId { get; set; }
        public string? Rewrite { get; set; }
    }
}

// EngineStatus lives with the models. this just keeps the client readable without a using alias
internal static class EngineStatusNames
{
    public const string Ok = Models.EngineStatus.Ok;
    public const string NotConfigured = Models.EngineStatus.NotConfigured;
    public const string Timeout = Models.EngineStatus.Timeout;
    public const string Failed = Models.EngineStatus.Failed;
}
