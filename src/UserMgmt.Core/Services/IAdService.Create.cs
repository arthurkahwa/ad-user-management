using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;

namespace UserMgmt.Core.Services;

/// <summary>
/// M1.4 slice: cross-store user-creation surface. AD-first, sidecar-second,
/// with partial-state flagged via <c>ReconciliationQueue</c> when the
/// sidecar write fails after the AD object exists.
/// </summary>
public partial interface IAdService
{
    /// <summary>
    /// Create a user in AD, mirror the firm-specific attributes into the
    /// sidecar, and surface partial state when the sidecar write fails
    /// after the AD object exists.
    /// </summary>
    /// <param name="dto">The new user. Contains both AD-bound fields and sidecar attributes.</param>
    /// <param name="password">
    /// The initial password. Sent to AD over LDAPS as the
    /// <c>unicodePwd</c> attribute (UTF-16LE quoted-string encoded) and never
    /// logged, audited, or surfaced in exception messages.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// <see cref="Result{T, TError}.Success"/> wrapping the created <see cref="AdUser"/>
    /// when both the AD and sidecar writes succeed, otherwise a typed
    /// <see cref="CreateUserError"/> describing the failure. Partial state
    /// (AD created, sidecar failed) is surfaced as
    /// <see cref="CreateUserError.PartialSuccess"/> carrying the created user.
    /// </returns>
    /// <exception cref="Ldap.LdapsRequiredException">
    /// Thrown when the bound <c>IAdConnection</c> port is not 636.
    /// </exception>
    Task<Result<AdUser, CreateUserError>> CreateAsync(
        NewUserDto dto,
        string password,
        CancellationToken cancellationToken = default);
}
