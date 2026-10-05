using System.Globalization;
using GreenClaimsGuard.Api.Data;
using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace GreenClaimsGuard.Api.Services;

public class PublicClaimsSeedService : IPublicClaimsSeedService
{
    private readonly AppDbContext _db;
    private readonly ILogger<PublicClaimsSeedService> _logger;
    private readonly bool _configured;

    public PublicClaimsSeedService(
        AppDbContext db,
        IConfiguration config,
        ILogger<PublicClaimsSeedService> logger)
    {
        _db = db;
        _logger = logger;
        _configured = !string.IsNullOrWhiteSpace(config.GetConnectionString("SqlServer"));
    }

    public async Task<PublicClaimsSeedImportResponse> SeedIfEmptyAsync(string csvPath)
    {
        if (!_configured)
        {
            return new PublicClaimsSeedImportResponse
            {
                Success = false,
                CsvPath = csvPath,
                Message = "Database is not configured. Set ConnectionStrings:SqlServer or SQL_SERVER_CONNECTION.",
            };
        }

        await EnsureTableAsync();
        var hasAnyRows = await _db.PublicClaimSeedRecords.AsNoTracking().AnyAsync();
        if (hasAnyRows)
        {
            return new PublicClaimsSeedImportResponse
            {
                Success = true,
                CsvPath = csvPath,
                Message = "Seed table already contains rows. Skipped startup seed import.",
            };
        }

        return await ImportAsync(csvPath);
    }

    public async Task<PublicClaimsSeedImportResponse> ImportAsync(string csvPath)
    {
        var response = new PublicClaimsSeedImportResponse
        {
            Success = false,
            CsvPath = csvPath,
        };

        if (!_configured)
        {
            response.Message = "Database is not configured. Set ConnectionStrings:SqlServer or SQL_SERVER_CONNECTION.";
            return response;
        }

        if (string.IsNullOrWhiteSpace(csvPath))
        {
            response.Message = "csvPath is required.";
            return response;
        }

        if (!File.Exists(csvPath))
        {
            response.Message = $"CSV file not found: {csvPath}";
            return response;
        }

        await EnsureTableAsync();

        var parsedRows = ParseRows(csvPath, response.Errors);
        response.TotalRows = parsedRows.Count;

        if (response.Errors.Count > 0)
        {
            response.Message = "CSV parsing failed. Fix the listed errors and retry.";
            return response;
        }

        var now = DateTime.UtcNow;
        var productIds = parsedRows
            .Select(row => row.ProductId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingRecords = await _db.PublicClaimSeedRecords
            .Where(row => productIds.Contains(row.ProductId))
            .ToListAsync();

        var existingByProductId = existingRecords
            .GroupBy(row => row.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in parsedRows)
        {
            existingByProductId.TryGetValue(row.ProductId, out var existing);

            if (existing is null)
            {
                _db.PublicClaimSeedRecords.Add(new PublicClaimSeedRecord
                {
                    ProductId = row.ProductId,
                    ClaimSentence = row.ClaimSentence,
                    IssueType = row.IssueType,
                    Baseline = row.Baseline,
                    ReductionPercentage = row.ReductionPercentage,
                    Timeframe = row.Timeframe,
                    EvidenceReference = row.EvidenceReference,
                    SourceUrl = row.SourceUrl,
                    SourceTitle = row.SourceTitle,
                    CapturedDate = row.CapturedDate,
                    CreatedAt = now,
                });
                response.Inserted++;
            }
            else
            {
                existing.ClaimSentence = row.ClaimSentence;
                existing.IssueType = row.IssueType;
                existing.Baseline = row.Baseline;
                existing.ReductionPercentage = row.ReductionPercentage;
                existing.Timeframe = row.Timeframe;
                existing.EvidenceReference = row.EvidenceReference;
                existing.SourceUrl = row.SourceUrl;
                existing.SourceTitle = row.SourceTitle;
                existing.CapturedDate = row.CapturedDate;
                response.Updated++;
            }
        }

        response.Skipped = Math.Max(0, response.TotalRows - response.Inserted - response.Updated);

        await _db.SaveChangesAsync();
        response.Success = true;
        response.Message = $"Imported {response.TotalRows} row(s): {response.Inserted} inserted, {response.Updated} updated.";
        return response;
    }

    private async Task EnsureTableAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(
            """
            IF OBJECT_ID(N'dbo.public_claims_seed', N'U') IS NULL
            BEGIN
                CREATE TABLE [dbo].[public_claims_seed](
                    [id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [product_id] NVARCHAR(128) NOT NULL,
                    [claim_sentence] NVARCHAR(MAX) NOT NULL,
                    [issue_type] NVARCHAR(128) NOT NULL,
                    [baseline] NVARCHAR(512) NULL,
                    [reduction_percentage] FLOAT NULL,
                    [timeframe] NVARCHAR(256) NULL,
                    [evidence_reference] NVARCHAR(512) NULL,
                    [source_url] NVARCHAR(1024) NOT NULL,
                    [source_title] NVARCHAR(512) NOT NULL,
                    [captured_date] DATE NOT NULL,
                    [created_at] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                );
                CREATE UNIQUE INDEX [IX_public_claims_seed_product_id] ON [dbo].[public_claims_seed]([product_id]);
            END
            """);
    }

    private static List<CsvSeedRow> ParseRows(string csvPath, List<string> errors)
    {
        var rows = new List<CsvSeedRow>();

        using var parser = new TextFieldParser(csvPath);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = false;

        if (parser.EndOfData)
        {
            errors.Add("CSV is empty.");
            return rows;
        }

        var headers = parser.ReadFields() ?? Array.Empty<string>();
        var indexByHeader = headers
            .Select((header, index) => new { header = (header ?? string.Empty).Trim(), index })
            .ToDictionary(x => x.header, x => x.index, StringComparer.OrdinalIgnoreCase);

        string[] requiredHeaders =
        [
            "productId",
            "claimSentence",
            "issueType",
            "baseline",
            "reductionPercentage",
            "timeframe",
            "evidenceReference",
            "sourceUrl",
            "sourceTitle",
            "capturedDate",
        ];

        foreach (var requiredHeader in requiredHeaders)
        {
            if (!indexByHeader.ContainsKey(requiredHeader))
            {
                errors.Add($"Missing required column: {requiredHeader}");
            }
        }

        if (errors.Count > 0)
        {
            return rows;
        }

        var rowNumber = 1;
        var seenProductIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!parser.EndOfData)
        {
            rowNumber++;
            string[] fields;
            try
            {
                fields = parser.ReadFields() ?? Array.Empty<string>();
            }
            catch (MalformedLineException ex)
            {
                errors.Add($"Row {rowNumber}: malformed CSV line ({ex.Message}).");
                continue;
            }

            string GetValue(string header)
            {
                var index = indexByHeader[header];
                if (index < 0 || index >= fields.Length) return string.Empty;
                return (fields[index] ?? string.Empty).Trim();
            }

            var productId = GetValue("productId");
            var claimSentence = GetValue("claimSentence");
            var issueType = GetValue("issueType");
            var sourceUrl = GetValue("sourceUrl");
            var sourceTitle = GetValue("sourceTitle");
            var capturedDateText = GetValue("capturedDate");

            if (string.IsNullOrWhiteSpace(productId))
            {
                errors.Add($"Row {rowNumber}: productId is required.");
                continue;
            }

            if (!seenProductIds.Add(productId))
            {
                errors.Add($"Row {rowNumber}: duplicate productId '{productId}' in CSV.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(claimSentence))
            {
                errors.Add($"Row {rowNumber}: claimSentence is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(issueType))
            {
                errors.Add($"Row {rowNumber}: issueType is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                errors.Add($"Row {rowNumber}: sourceUrl is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(sourceTitle))
            {
                errors.Add($"Row {rowNumber}: sourceTitle is required.");
                continue;
            }

            if (!DateTime.TryParseExact(capturedDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var capturedDate))
            {
                errors.Add($"Row {rowNumber}: capturedDate must be yyyy-MM-dd.");
                continue;
            }

            var reductionText = GetValue("reductionPercentage");
            double? reductionPercentage = null;
            if (!string.IsNullOrWhiteSpace(reductionText))
            {
                if (double.TryParse(reductionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedReduction))
                {
                    reductionPercentage = parsedReduction;
                }
                else
                {
                    errors.Add($"Row {rowNumber}: reductionPercentage is not a valid number.");
                    continue;
                }
            }

            rows.Add(new CsvSeedRow
            {
                ProductId = productId,
                ClaimSentence = claimSentence,
                IssueType = issueType,
                Baseline = NullIfWhiteSpace(GetValue("baseline")),
                ReductionPercentage = reductionPercentage,
                Timeframe = NullIfWhiteSpace(GetValue("timeframe")),
                EvidenceReference = NullIfWhiteSpace(GetValue("evidenceReference")),
                SourceUrl = sourceUrl,
                SourceTitle = sourceTitle,
                CapturedDate = capturedDate,
            });
        }

        return rows;
    }

    private static string? NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class CsvSeedRow
    {
        public string ProductId { get; set; } = "";
        public string ClaimSentence { get; set; } = "";
        public string IssueType { get; set; } = "";
        public string? Baseline { get; set; }
        public double? ReductionPercentage { get; set; }
        public string? Timeframe { get; set; }
        public string? EvidenceReference { get; set; }
        public string SourceUrl { get; set; } = "";
        public string SourceTitle { get; set; } = "";
        public DateTime CapturedDate { get; set; }
    }
}
