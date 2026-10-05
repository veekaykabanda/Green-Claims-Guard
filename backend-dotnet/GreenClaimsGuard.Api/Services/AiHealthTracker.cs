using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// remembers how the last AI check went, so the health ribbon can show the truth without paying for a call on every poll
public class AiHealthTracker
{
    // a failure this old doesn't mean the AI is still down now
    public static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private string? _lastStatus;
    private DateTime? _lastAt;

    public bool Configured { get; set; }

    public void Record(string status)
    {
        lock (_lock)
        {
            _lastStatus = status;
            _lastAt = DateTime.UtcNow;
        }
    }

    public (string Status, DateTime? LastCheckedAt) Snapshot()
    {
        if (!Configured) return (EngineStatus.NotConfigured, null);

        lock (_lock)
        {
            if (_lastStatus is null) return (EngineStatus.Ok, null);
            if (_lastStatus != EngineStatus.Ok && DateTime.UtcNow - _lastAt > FailureMemory)
                return (EngineStatus.Ok, _lastAt);
            return (_lastStatus, _lastAt);
        }
    }
}
