using System.Collections.Concurrent;

namespace GreenClaimsGuard.Api.Services;

// limits how many AI checks one person can run per minute, so a stuck page can't burn the OpenAI budget. rules-only checks are free and don't count here
public class AiBudget
{
    public const int PerMinute = 10;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTime WindowStart, int Used)> _usage = new();

    public bool TryTake(string userId, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var allowed = false;

        _usage.AddOrUpdate(
            userId,
            _ =>
            {
                allowed = true;
                return (at, 1);
            },
            (_, current) =>
            {
                if (at - current.WindowStart >= Window)
                {
                    allowed = true;
                    return (at, 1);
                }

                allowed = current.Used < PerMinute;
                return allowed ? (current.WindowStart, current.Used + 1) : current;
            });

        return allowed;
    }
}
