namespace UserMgmt.Core.Common;

/// <summary>
/// Tagged union of failures returned by <c>IAdService.ResetPasswordAsync</c>.
/// </summary>
/// <remarks>
/// The LDAPS refusal (<c>LdapsRequiredException</c>) is a hard precondition
/// failure and is surfaced as an exception, not a member of this union.
/// Password writes go through a single <c>ModifyRequest</c> that the server
/// accepts atomically, so there is no in-service CAS — the
/// <see cref="ConcurrencyConflict"/> case is included for forward
/// compatibility with future write paths but is not raised by the current
/// M1.6 implementation.
/// </remarks>
public abstract record ResetPasswordError
{
    private ResetPasswordError()
    {
    }

    /// <summary>No AD user matches the supplied UPN.</summary>
    /// <param name="Upn">The UPN that was queried.</param>
    public sealed record UserNotFound(string Upn) : ResetPasswordError;

    /// <summary>
    /// Reserved for future use. The current implementation does not perform
    /// attribute-level CAS on the password write — AD's
    /// <c>unicodePwd</c> write is server-atomic via
    /// <c>DirectoryAttributeOperation.Replace</c>.
    /// </summary>
    /// <param name="Attribute">The AD attribute whose state has drifted.</param>
    /// <param name="CurrentValue">The value currently present on the server, if known.</param>
    public sealed record ConcurrencyConflict(string Attribute, string? CurrentValue) : ResetPasswordError;
}
