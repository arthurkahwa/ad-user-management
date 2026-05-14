using System.Diagnostics.CodeAnalysis;

namespace UserMgmt.Data.Entities;

/// <summary>
/// Allowed values for <see cref="ReconciliationQueue.Status"/>.
/// </summary>
public static class ReconciliationStatus
{
    /// <summary>The reconciliation has been recorded but not yet resolved.</summary>
    public const string Open = "Open";

    /// <summary>An admin has resolved the reconciliation (manually or via a follow-up).</summary>
    public const string Resolved = "Resolved";

    /// <summary>An admin has cancelled the reconciliation without resolving it.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// One row per partial-state failure that an admin must resolve.
/// </summary>
/// <remarks>
/// M1 ships only the entity and the partial-state flagging in <c>AdService.CreateAsync</c>.
/// The actual queue-processing hosted service belongs to a later milestone.
/// See README §Cross-store consistency.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "ReconciliationQueue is a domain term from the spec, not a collection type.")]
public sealed class ReconciliationQueue
{
    /// <summary>Identity column; assigned by SQL on insert.</summary>
    public long Id { get; set; }

    /// <summary>UTC timestamp when the partial-state was recorded.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>UPN of the user the partial-state affects.</summary>
    public string TargetUpn { get; set; } = string.Empty;

    /// <summary>
    /// Operation tag describing the partial-state shape
    /// (e.g. <c>CreateUser-SidecarMissing</c>).
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>JSON payload with the original attempt details.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Status (<see cref="ReconciliationStatus.Open"/> /
    /// <see cref="ReconciliationStatus.Resolved"/> /
    /// <see cref="ReconciliationStatus.Cancelled"/>).
    /// </summary>
    public string Status { get; set; } = ReconciliationStatus.Open;

    /// <summary>UPN of the admin who resolved this entry, if any.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>UTC timestamp when the entry was resolved.</summary>
    public DateTime? ResolvedAt { get; set; }
}
