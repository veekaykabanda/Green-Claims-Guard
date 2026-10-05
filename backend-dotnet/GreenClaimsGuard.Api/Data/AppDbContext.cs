using GreenClaimsGuard.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace GreenClaimsGuard.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AnalysisLog> AnalysisLogs => Set<AnalysisLog>();
    public DbSet<ComplianceReview> ComplianceReviews => Set<ComplianceReview>();
    public DbSet<PublicClaimSeedRecord> PublicClaimSeedRecords => Set<PublicClaimSeedRecord>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<AuditEntry> AuditLedger => Set<AuditEntry>();
    public DbSet<ProductDraft> ProductDrafts => Set<ProductDraft>();
    public DbSet<FactsRequest> FactsRequests => Set<FactsRequest>();
    public DbSet<UserDirectoryEntry> UserDirectory => Set<UserDirectoryEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AnalysisLog>(entity =>
        {
            entity.ToTable("analysis_logs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.InputText).HasColumnName("input_text").IsRequired();
            entity.Property(e => e.OverallRisk).HasColumnName("overall_risk").HasMaxLength(32).IsRequired();
            entity.Property(e => e.TrafficLight).HasColumnName("traffic_light").HasMaxLength(32).IsRequired();
            entity.Property(e => e.ComplianceScore).HasColumnName("compliance_score");
            entity.Property(e => e.TotalIssues).HasColumnName("total_issues");
            entity.Property(e => e.RuleFindingsJson).HasColumnName("rule_findings_json").IsRequired();
            entity.Property(e => e.GroupedFindingsJson).HasColumnName("grouped_findings_json");
            entity.Property(e => e.AiExplanation).HasColumnName("ai_explanation");
            entity.Property(e => e.SuggestedRewrite).HasColumnName("suggested_rewrite");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("GETUTCDATE()");
            // added this index because sorting without it got too slow once the table grew
            entity.HasIndex(e => e.CreatedAt);
        });

        modelBuilder.Entity<ComplianceReview>(entity =>
        {
            entity.ToTable("compliance_reviews");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ProductId).HasColumnName("product_id").IsRequired();
            entity.Property(e => e.ActorUserId).HasColumnName("actor_user_id").HasMaxLength(200);
            entity.Property(e => e.Market).HasColumnName("market").HasMaxLength(8);
            entity.Property(e => e.RulesVersion).HasColumnName("rules_version").HasMaxLength(64);
            entity.Property(e => e.AiStatus).HasColumnName("ai_status").HasMaxLength(32);
            entity.Property(e => e.OpenIssueCount).HasColumnName("open_issue_count");
            entity.Property(e => e.OverrideReason).HasColumnName("override_reason").HasMaxLength(1000);
            entity.Property(e => e.NeedsOverride).HasColumnName("needs_override");
            entity.Property(e => e.ProductName).HasColumnName("product_name").HasMaxLength(200);
            entity.Property(e => e.Category).HasColumnName("category").HasMaxLength(200);
            entity.Property(e => e.Subcategory).HasColumnName("subcategory").HasMaxLength(200);
            entity.Property(e => e.Tags).HasColumnName("tags").HasMaxLength(500);
            entity.Property(e => e.SendBackReason).HasColumnName("send_back_reason").HasMaxLength(40);
            entity.Property(e => e.SendBackComment).HasColumnName("send_back_comment").HasMaxLength(1000);
            entity.Property(e => e.FinalDescription).HasColumnName("final_description").IsRequired();
            entity.Property(e => e.OverallStatus).HasColumnName("overall_status").HasMaxLength(64).IsRequired();
            entity.Property(e => e.DecisionsJson).HasColumnName("decisions_json").IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("GETUTCDATE()");
            entity.HasOne(e => e.Product)
                .WithMany()
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.ProductId, e.CreatedAt });
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_ledger");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.Action).HasColumnName("action").HasMaxLength(16).IsRequired();
            entity.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(16).IsRequired();
            entity.Property(e => e.CopySnapshot).HasColumnName("copy_snapshot").IsRequired();
            entity.Property(e => e.CopyHash).HasColumnName("copy_hash").HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
            entity.Property(e => e.Market).HasColumnName("market").HasMaxLength(8).IsRequired();
            entity.Property(e => e.RulesStatus).HasColumnName("rules_status").HasMaxLength(16).IsRequired();
            entity.Property(e => e.AiStatus).HasColumnName("ai_status").HasMaxLength(16).IsRequired();
            entity.Property(e => e.RulesVersion).HasColumnName("rules_version").HasMaxLength(64).IsRequired();
            entity.Property(e => e.IssuesJson).HasColumnName("issues_json").IsRequired();
            entity.Property(e => e.Justification).HasColumnName("justification");
            entity.Property(e => e.Detail).HasColumnName("detail");
            entity.Property(e => e.UserId).HasColumnName("user_id").HasMaxLength(200);
            entity.Property(e => e.Timestamp).HasColumnName("occurred_at").HasDefaultValueSql("GETUTCDATE()");
            entity.HasOne(e => e.Product)
                .WithMany()
                .HasForeignKey(e => e.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(e => new { e.ProductId, e.Timestamp });
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Sku).HasColumnName("sku").HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("GETUTCDATE()");
            entity.Property(e => e.CreatedByUserId).HasColumnName("created_by_user_id").HasMaxLength(200);
            entity.Property(e => e.Origin).HasColumnName("origin").HasMaxLength(200);
            entity.Property(e => e.FactsVerifiedByUserId).HasColumnName("facts_verified_by_user_id").HasMaxLength(200);
            entity.Property(e => e.FactsVerifiedAt).HasColumnName("facts_verified_at");
            entity.HasMany(e => e.Materials).WithOne().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Certifications).WithOne().HasForeignKey(c => c.ProductId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProductMaterial>(entity =>
        {
            entity.ToTable("product_materials");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.Material).HasColumnName("material").HasMaxLength(100).IsRequired();
            entity.Property(e => e.Percentage).HasColumnName("percentage").HasPrecision(5, 2);
            entity.HasIndex(e => e.ProductId);
        });

        modelBuilder.Entity<ProductCertification>(entity =>
        {
            entity.ToTable("product_certifications");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.ProductId);
        });

        modelBuilder.Entity<UserDirectoryEntry>(entity =>
        {
            entity.ToTable("user_directory");
            entity.HasKey(e => e.UserId);
            entity.Property(e => e.UserId).HasColumnName("user_id").HasMaxLength(200);
            entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
            entity.Property(e => e.LastSeenAt).HasColumnName("last_seen_at");
        });

        modelBuilder.Entity<FactsRequest>(entity =>
        {
            entity.ToTable("facts_requests");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.RequestedByUserId).HasColumnName("requested_by_user_id").HasMaxLength(200).IsRequired();
            entity.Property(e => e.Note).HasColumnName("note").HasMaxLength(500).IsRequired();
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");
            entity.HasOne(e => e.Product).WithMany().HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.ProductId, e.CreatedAt });
        });

        modelBuilder.Entity<ProductDraft>(entity =>
        {
            entity.ToTable("product_drafts");
            entity.HasKey(e => e.ProductId);
            entity.Property(e => e.ProductId).HasColumnName("product_id").ValueGeneratedNever();
            entity.Property(e => e.Text).HasColumnName("text").HasMaxLength(5000).IsRequired();
            entity.Property(e => e.Market).HasColumnName("market").HasMaxLength(8).IsRequired();
            entity.Property(e => e.SavedAt).HasColumnName("saved_at");
            entity.HasOne(e => e.Product).WithOne().HasForeignKey<ProductDraft>(e => e.ProductId).OnDelete(DeleteBehavior.Cascade);
            // nightly cleanup uses this to find old drafts
            entity.HasIndex(e => e.SavedAt);
        });

        modelBuilder.Entity<PublicClaimSeedRecord>(entity =>
        {
            entity.ToTable("public_claims_seed");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ProductId).IsUnique();
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.ProductId).HasColumnName("product_id").HasMaxLength(128).IsRequired();
            entity.Property(e => e.ClaimSentence).HasColumnName("claim_sentence").IsRequired();
            entity.Property(e => e.IssueType).HasColumnName("issue_type").HasMaxLength(128).IsRequired();
            entity.Property(e => e.Baseline).HasColumnName("baseline");
            entity.Property(e => e.ReductionPercentage).HasColumnName("reduction_percentage");
            entity.Property(e => e.Timeframe).HasColumnName("timeframe");
            entity.Property(e => e.EvidenceReference).HasColumnName("evidence_reference");
            entity.Property(e => e.SourceUrl).HasColumnName("source_url").HasMaxLength(1024).IsRequired();
            entity.Property(e => e.SourceTitle).HasColumnName("source_title").HasMaxLength(512).IsRequired();
            entity.Property(e => e.CapturedDate).HasColumnName("captured_date").HasColumnType("date");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("GETUTCDATE()");
        });
    }
}

public class AnalysisLog
{
    public int Id { get; set; }
    public string InputText { get; set; } = "";
    public string OverallRisk { get; set; } = "";
    public string TrafficLight { get; set; } = "";
    public int? ComplianceScore { get; set; }
    public int? TotalIssues { get; set; }
    public string RuleFindingsJson { get; set; } = "[]";
    public string? GroupedFindingsJson { get; set; }
    public string? AiExplanation { get; set; }
    public string? SuggestedRewrite { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ComplianceReview
{
    public int Id { get; set; }
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    // who did this and what the checks said at the time
    public string? ActorUserId { get; set; }
    public string? Market { get; set; }
    public string? RulesVersion { get; set; }
    public string? AiStatus { get; set; }
    public int? OpenIssueCount { get; set; }
    public string? OverrideReason { get; set; }
    // true when the writer kept a critical issue with a reason, needs editor approval to publish
    public bool NeedsOverride { get; set; }
    // name as it was submitted, since the name counts as part of the copy
    public string? ProductName { get; set; }
    // category, subcategory and tags as submitted, publish re-check reads these not the live product
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public string? Tags { get; set; }
    // only set on a send back, the rule the editor flagged and why
    public string? SendBackReason { get; set; }
    public string? SendBackComment { get; set; }
    public string FinalDescription { get; set; } = "";
    public string OverallStatus { get; set; } = "";
    public string DecisionsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class PublicClaimSeedRecord
{
    public int Id { get; set; }
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
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
