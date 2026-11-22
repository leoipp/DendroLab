namespace Dendro.Core.Auditing;

/// <summary>Audit state and evidence stored separately from user project files.</summary>
public static class AuditStates
{
    public const string Identified = "Identificado";
    public const string Investigating = "Em investigação";
    public const string Confirmed = "Confirmado";
    public const string Proposed = "Proposta elaborada";
    public const string AwaitingApproval = "Aguardando aprovação";
    public const string Approved = "Aprovado";
    public const string Rejected = "Rejeitado";
    public const string InImplementation = "Em implementação";
    public const string InValidation = "Em validação";
    public const string Resolved = "Resolvido";
    public const string Reopened = "Reaberto";
    public const string Archived = "Arquivado";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Identified, Investigating, Confirmed, Proposed, AwaitingApproval, Approved,
        Rejected, InImplementation, InValidation, Resolved, Reopened, Archived
    };
}

public sealed record AuditRun(
    string Id,
    string Scope,
    string ApplicationVersion,
    string? Commit,
    string Environment,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    string Summary,
    string Methodology
);

public sealed record AuditFinding(
    string Id,
    string RunId,
    string Agent,
    string Module,
    string Category,
    string Description,
    string Severity,
    string Status,
    string Evidence,
    string? Reproduction,
    string? RootCause,
    double Confidence,
    DateTimeOffset IdentifiedAt
);

public sealed record AuditRecommendation(
    string Id,
    string? FindingId,
    string Title,
    string Description,
    string Category,
    string Severity,
    string Status,
    string ProposedChange,
    string Evidence,
    string? Risks,
    string? AcceptanceCriteria,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);

public sealed record AuditEvent(
    string Id,
    string EntityType,
    string EntityId,
    string Action,
    string PreviousState,
    string NewState,
    string Actor,
    string Details,
    DateTimeOffset At
);
