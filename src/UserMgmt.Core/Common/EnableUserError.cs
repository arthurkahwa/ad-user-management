namespace UserMgmt.Core.Common;

/// <summary>
/// Tagged union of failures returned by <c>IAdService.SetEnabledAsync</c>.
/// </summary>
/// <remarks>
/// Inherits the shared <see cref="UserNotFound"/> and
/// <see cref="ConcurrencyConflict"/> records by composition — they are
/// reused across services so callers can render a single banner
/// regardless of which operation produced the failure. The
/// <see cref="InvalidReason"/> case covers a non-null
/// <c>reason</c> that is outside <see cref="ValidReasons.All"/>;
/// the service short-circuits before any AD traffic in that case.
/// </remarks>
public abstract record EnableUserError
{
    private EnableUserError()
    {
    }

    /// <summary>No AD user matches the supplied UPN.</summary>
    /// <param name="Upn">The UPN that was queried.</param>
    public sealed record UserNotFound(string Upn) : EnableUserError;

    /// <summary>
    /// The supplied <c>reason</c> is not in <see cref="ValidReasons.All"/>.
    /// Surfaced before any directory traffic so the bad reason never lands
    /// in an audit row and never bumps the CHECK constraint.
    /// </summary>
    /// <param name="Reason">The reason value the caller supplied.</param>
    public sealed record InvalidReason(string Reason) : EnableUserError;

    /// <summary>
    /// Optimistic concurrency check on the <c>userAccountControl</c> bit-flip
    /// failed — another writer modified the attribute between the read and the
    /// delete-old/add-new write.
    /// </summary>
    /// <param name="Attribute">The AD attribute whose state has drifted.</param>
    /// <param name="CurrentValue">The value currently present on the server, if known.</param>
    public sealed record ConcurrencyConflict(string Attribute, string? CurrentValue) : EnableUserError;
}
