using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

// data imports write reference data, so they only run somewhere safe (a dev machine), and every run gets logged with who did it and what happened
public static class DataImportRules
{
    public const string RefusedDetail = "Data imports only run in the Development environment.";

    public static bool IsAllowed(IHostEnvironment environment) => environment.IsDevelopment();

    public static AuditEntry AuditRow(string? userId, bool succeeded, string detail) => new()
    {
        Action = AuditActions.DataTransfer,
        Outcome = succeeded ? AuditOutcomes.Ok : AuditOutcomes.Blocked,
        CopySnapshot = string.Empty,
        CopyHash = AuditEntryFactory.HashOf(string.Empty),
        Detail = detail,
        UserId = userId,
    };
}
