using GreenClaimsGuard.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

// drafts untouched for 90 days get deleted, submitting a draft deletes it straight away too, so what's left here is just abandoned work
public static class DraftRetention
{
    public const int DefaultDays = 90;

    // Returns how many drafts were removed.
    public static async Task<int> PurgeAsync(AppDbContext db, DateTime nowUtc, int retentionDays = DefaultDays)
    {
        var cutoff = nowUtc.AddDays(-retentionDays);
        return await db.ProductDrafts.Where(d => d.SavedAt < cutoff).ExecuteDeleteAsync();
    }
}

// runs cleanup once at startup then once a day, a failure just gets logged and retried tomorrow since leftover drafts are harmless and this should never stop the app
public class DraftCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IDbService _dbService;
    private readonly IConfiguration _config;
    private readonly ILogger<DraftCleanupService> _logger;

    public DraftCleanupService(IServiceScopeFactory scopes, IDbService dbService, IConfiguration config, ILogger<DraftCleanupService> logger)
    {
        _scopes = scopes;
        _dbService = dbService;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunOnceAsync();
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunOnceAsync()
    {
        if (!_dbService.IsConfigured) return;

        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var days = _config.GetValue<int?>("Drafts:RetentionDays") ?? DraftRetention.DefaultDays;
            var removed = await DraftRetention.PurgeAsync(db, DateTime.UtcNow, days);
            if (removed > 0) _logger.LogInformation("Deleted {Count} draft(s) untouched for {Days} days", removed, days);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Draft cleanup failed; it will run again tomorrow");
        }
    }
}
