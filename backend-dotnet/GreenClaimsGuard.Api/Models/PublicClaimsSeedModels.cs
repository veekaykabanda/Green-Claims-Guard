namespace GreenClaimsGuard.Api.Models;

public class PublicClaimsSeedImportRequest
{
    public string? CsvPath { get; set; }
}

public class PublicClaimsSeedImportResponse
{
    public bool Success { get; set; }
    public string CsvPath { get; set; } = "";
    public int TotalRows { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public string Message { get; set; } = "";
    public List<string> Errors { get; set; } = new();
}

