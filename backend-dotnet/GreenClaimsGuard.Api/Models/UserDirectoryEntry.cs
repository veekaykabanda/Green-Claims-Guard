namespace GreenClaimsGuard.Api.Models;

// maps a login id to an email just for display (like in the review queue), access checks and the audit log still use the id
public class UserDirectoryEntry
{
    public string UserId { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
