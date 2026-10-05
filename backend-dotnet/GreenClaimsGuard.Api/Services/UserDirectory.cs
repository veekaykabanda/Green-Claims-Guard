using System.Collections.Concurrent;
using System.Security.Claims;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using GreenClaimsGuard.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Services;

// keeps each user's email in memory so we're not writing to the db on every request. if saving it fails we just move on
public class UserDirectory
{
    public const int MaxEmailLength = 320;

    private readonly ConcurrentDictionary<string, string> _recorded = new();
    private readonly ILogger<UserDirectory> _logger;

    public UserDirectory(ILogger<UserDirectory> logger)
    {
        _logger = logger;
    }

    public async Task RecordAsync(ClaimsPrincipal user, AppDbContext db, IDbService dbService)
    {
        var id = CurrentUser.GetId(user);
        var email = CurrentUser.GetEmail(user);
        if (id is null || email is null) return;
        if (_recorded.TryGetValue(id, out var known) && known == email) return;
        if (!dbService.IsConfigured) return;

        try
        {
            var entry = await db.UserDirectory.FindAsync(id);
            if (entry is null)
            {
                db.UserDirectory.Add(new UserDirectoryEntry { UserId = id, Email = email, LastSeenAt = DateTime.UtcNow });
            }
            else
            {
                entry.Email = email;
                entry.LastSeenAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            _recorded[id] = email;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not record the email for a signed-in user");
        }
    }
}
