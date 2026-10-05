using GreenClaimsGuard.Api.Models;

namespace GreenClaimsGuard.Api.Services;

public enum ComplianceAction
{
    Analyze,   // a check while writing
    Submit,    // handing the copy over for compliance review
    Publish    // final sign-off
}

public class ComplianceRequest
{
    public string Text { get; init; } = "";
    public ComplianceAction Action { get; init; } = ComplianceAction.Analyze;

    // only matters for Analyze (live typing). Submit and Publish always run the AI check
    public bool RulesOnly { get; init; }

    public string? Industry { get; init; }

    // product the copy is for. if set, loads its verified facts and checks the copy against them
    public Guid? ProductId { get; init; }

    // product name gets checked too, since a name can be an environmental claim
    public string? ProductName { get; init; }

    // Category, subcategory and tags from the Product Details card, checked the same way as the name.
    public string? ProductCategory { get; init; }
    public string? ProductSubcategory { get; init; }
    public string? ProductTags { get; init; }
    public string Market { get; init; } = Markets.Uk;
    public List<IssueDecisionInput> Decisions { get; init; } = new();
    public ProductFacts? ProductFacts { get; init; }
}

// the one place a verdict gets decided. analyze, submit and publish all go through this so we never trust what the client says it got
public interface IComplianceOrchestrator
{
    Task<AnalyzeResponse> EvaluateAsync(ComplianceRequest request);
}
