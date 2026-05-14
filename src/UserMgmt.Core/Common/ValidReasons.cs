namespace UserMgmt.Core.Common;

/// <summary>
/// The set of reason codes that may be recorded on a Disable / Delete audit row.
/// </summary>
/// <remarks>
/// Mirrors the database-level <c>CHECK</c> constraint enforced on
/// <c>AuditEntry.Reason</c> in <c>UserMgmt.Data</c>; surfaced in
/// <c>UserMgmt.Core</c> so service-layer validation can short-circuit a bad
/// value before the DB rejects it. The canonical string constants live in
/// <c>UserMgmt.Data.Entities.AuditReason</c> and are duplicated here to
/// keep <c>UserMgmt.Core</c> free of a <c>UserMgmt.Data</c> dependency.
/// Keep the two in sync — a guard test in <c>UserMgmt.Core.Tests</c> compares
/// the two sets.
/// </remarks>
public static class ValidReasons
{
    /// <summary>Account has been inactive long enough to be considered dormant.</summary>
    public const string Stale = "Stale";

    /// <summary>User has left the organisation.</summary>
    public const string Termination = "Termination";

    /// <summary>Reorganisation moved the user out of scope for this OU.</summary>
    public const string Reorg = "Reorg";

    /// <summary>Account suspected to be compromised; closed for security reasons.</summary>
    public const string Compromise = "Compromise";

    /// <summary>All allowed reasons, in canonical order.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Stale, Termination, Reorg, Compromise };

    /// <summary>
    /// Test whether <paramref name="reason"/> is in the allowed set.
    /// </summary>
    /// <param name="reason">The candidate reason code, or null.</param>
    /// <returns>
    /// True when <paramref name="reason"/> is one of the allowed values;
    /// false otherwise. Null returns false — callers must short-circuit on
    /// null before consulting this method.
    /// </returns>
    public static bool Contains(string? reason) =>
        reason is not null && All.Contains(reason);
}
