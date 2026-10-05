using System.Text.Json;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

public class DbService : IDbService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DbService> _logger;
    private readonly bool _configured;

    public DbService(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<DbService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configured = !string.IsNullOrWhiteSpace(config.GetConnectionString("SqlServer"));
    }

    public bool IsConfigured => _configured;

    public async Task<bool> IsAvailableAsync()
    {
        if (!_configured) return false;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Database.CanConnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database availability check failed");
            return false;
        }
    }

    public async Task LogAnalysisAsync(string text, AnalyzeResponse result, List<RuleFinding> ruleFindings)
    {
        if (!_configured) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var log = new AnalysisLog
            {
                InputText = text,
                OverallRisk = result.OverallRisk ?? "",
                TrafficLight = result.TrafficLight ?? "",
                ComplianceScore = result.ComplianceScore,
                TotalIssues = result.TotalIssues,
                RuleFindingsJson = JsonSerializer.Serialize(ruleFindings),
                GroupedFindingsJson = result.GroupedFindings != null
                    ? JsonSerializer.Serialize(result.GroupedFindings)
                    : null,
                AiExplanation = result.AiExplanation,
                SuggestedRewrite = result.SuggestedRewrite,
                CreatedAt = DateTime.UtcNow
            };

            db.AnalysisLogs.Add(log);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQL Server logAnalysis error");
        }
    }

    public async Task<DbStatus> CheckStatusAsync()
    {
        if (!_configured)
        {
            return new DbStatus
            {
                Configured = false,
                Connected = false,
                TableExists = false,
                Message = "Database not configured (set ConnectionStrings:SqlServer in appsettings.json to enable)."
            };
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var canConnect = await db.Database.CanConnectAsync();
            if (!canConnect)
            {
                return new DbStatus
                {
                    Configured = true,
                    Connected = false,
                    TableExists = false,
                    Message = "Database configured but connection failed."
                };
            }

            // Check if table exists by trying a query
            bool tableExists;
            try
            {
                await db.AnalysisLogs.Take(1).CountAsync();
                tableExists = true;
            }
            catch
            {
                tableExists = false;
            }

            return new DbStatus
            {
                Configured = true,
                Connected = true,
                TableExists = tableExists,
                Message = tableExists
                    ? "Database connected and table AnalysisLogs exists."
                    : "Database connected. Table will be created on first migration."
            };
        }
        catch (Exception ex)
        {
            return new DbStatus
            {
                Configured = true,
                Connected = false,
                TableExists = false,
                Message = $"Connection failed: {ex.Message}"
            };
        }
    }

}
