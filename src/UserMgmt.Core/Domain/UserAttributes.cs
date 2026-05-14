using UserMgmt.Core.Auth;

namespace UserMgmt.Core.Domain;

/// <summary>
/// Sidecar attributes for an AD user that are not mirrored into AD itself.
/// Keyed by <see cref="Upn"/>; updated under optimistic concurrency via
/// <see cref="RowVersion"/>.
/// </summary>
/// <remarks>
/// Lives in <c>UserMgmt.Core/Domain/</c> rather than <c>UserMgmt.Data/Entities/</c>
/// so the <see cref="UserMgmt.Core.Services.IAttributeService"/> can return it
/// without Core taking a dependency on Data. <c>UserMgmt.Data</c> configures
/// the persistence mapping (table name, max lengths, concurrency token) in
/// <c>UserMgmtDbContext.OnModelCreating</c>.
/// </remarks>
public sealed class UserAttributes
{
    /// <summary>UPN — also the primary key.</summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>External employee identifier from the HR system.</summary>
    public string? EmployeeId { get; set; }

    /// <summary>Cost-centre code for accounting attribution.</summary>
    public string? CostCenter { get; set; }

    /// <summary>Contract type (e.g. <c>Permanent</c>, <c>Contractor</c>, <c>Intern</c>).</summary>
    public string? ContractType { get; set; }

    /// <summary>
    /// Last computed stale-risk score. Excluded from the audit log because it
    /// changes on every retrain and would otherwise drown signal-bearing entries.
    /// </summary>
    [AuditIgnore]
    public float StaleRiskScore { get; set; }

    /// <summary>
    /// True if this user should be excluded from ML training and scoring.
    /// Persisted in SQL rather than AD because it is system-internal, not org-wide.
    /// </summary>
    public bool ExcludeFromMLScoring { get; set; }

    /// <summary>
    /// Optimistic-concurrency token for this row. An app-bumped <see cref="Guid"/>
    /// rather than a SQL Server <c>rowversion</c>, so the same concurrency
    /// mechanism works identically across SQL Server, SQLite, and the EF Core
    /// in-memory provider — see <c>docs/ARCHITECTURE-NOTES.md</c> for the
    /// rationale. Rotated by <see cref="UserMgmt.Core.Services.IAttributeService"/>
    /// on every write; never modified by callers directly. Excluded from the
    /// audit log because it is a control-plane token, not business data.
    /// </summary>
    [AuditIgnore]
    public Guid RowVersion { get; set; }
}
