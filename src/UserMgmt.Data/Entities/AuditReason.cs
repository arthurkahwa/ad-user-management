namespace UserMgmt.Data.Entities;

/// <summary>
/// Allowed values for <see cref="AuditEntry.Reason"/>.
/// </summary>
/// <remarks>
/// These values are also enforced at the database level via a <c>CHECK</c>
/// constraint, so an EF Core migration must replay this set when adding a
/// new reason. Keep the constants and the constraint in sync.
/// </remarks>
public static class AuditReason
{
    /// <summary>Account has been inactive long enough to be considered dormant.</summary>
    public const string Stale = "Stale";

    /// <summary>User has left the organisation.</summary>
    public const string Termination = "Termination";

    /// <summary>Reorganisation moved the user out of scope for this OU.</summary>
    public const string Reorg = "Reorg";

    /// <summary>Account suspected to be compromised; closed for security reasons.</summary>
    public const string Compromise = "Compromise";

    /// <summary>The full set of allowed reasons, in canonical order.</summary>
    public static IReadOnlyList<string> AllowedValues { get; } =
        [Stale, Termination, Reorg, Compromise];
}
