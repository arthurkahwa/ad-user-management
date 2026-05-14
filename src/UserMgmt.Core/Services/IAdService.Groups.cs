using UserMgmt.Core.Common;

namespace UserMgmt.Core.Services;

/// <summary>
/// Group-membership write surface added in M1.7. Lives in a partial file so
/// it can land alongside the read path (M1.2) and the other write slices
/// (M1.4–M1.6) without serialising on a single <c>IAdService.cs</c> file.
/// </summary>
public partial interface IAdService
{
    /// <summary>
    /// Add a user (by UPN) to a group (by DN).
    /// </summary>
    /// <param name="upn">UPN of the user to add.</param>
    /// <param name="groupDn">Distinguished name of the group.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>
    /// <c>Success(Unit)</c> when the user was added.
    /// <see cref="UserNotFound"/> when no user matches the UPN.
    /// <see cref="GroupNotFound"/> when no group matches the DN.
    /// <see cref="AlreadyMember"/> when the user is already in the group
    /// (no AD write is issued; idempotent).
    /// </returns>
    Task<Result<Unit, GroupMembershipError>> AddToGroupAsync(
        string upn,
        string groupDn,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a user (by UPN) from a group (by DN).
    /// </summary>
    /// <param name="upn">UPN of the user to remove.</param>
    /// <param name="groupDn">Distinguished name of the group.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>
    /// <c>Success(Unit)</c> when the user was removed.
    /// <see cref="UserNotFound"/> when no user matches the UPN.
    /// <see cref="GroupNotFound"/> when no group matches the DN.
    /// <see cref="NotAMember"/> when the user is not in the group
    /// (no AD write is issued; idempotent).
    /// </returns>
    Task<Result<Unit, GroupMembershipError>> RemoveFromGroupAsync(
        string upn,
        string groupDn,
        CancellationToken cancellationToken = default);
}
