using System.ComponentModel.DataAnnotations;
using UserMgmt.Core.Auth;

namespace UserMgmt.Data.Entities;

/// <summary>
/// Sidecar attributes for an AD user that are not mirrored into AD itself.
/// Keyed by <see cref="Upn"/>; updated under optimistic concurrency via
/// <see cref="RowVersion"/>.
/// </summary>
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

    /// <summary>SQL Server <c>rowversion</c> token for optimistic concurrency.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }
}
