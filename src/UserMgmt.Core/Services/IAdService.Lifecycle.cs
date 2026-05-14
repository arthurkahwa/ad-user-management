using UserMgmt.Core.Common;

namespace UserMgmt.Core.Services;

/// <summary>
/// M1.6 lifecycle operations on <see cref="IAdService"/> — enable / disable
/// and password reset. Lives in a partial-interface file so it can land
/// alongside #5 (Create), #6 (Update), and #8 (Group) without merge conflict.
/// </summary>
public partial interface IAdService
{
    /// <summary>
    /// Flip the <c>ACCOUNTDISABLE</c> bit on the user's
    /// <c>userAccountControl</c> attribute.
    /// </summary>
    /// <param name="upn">UPN of the user to enable or disable.</param>
    /// <param name="enabled">
    /// True to enable (clear the bit); false to disable (set the bit).
    /// </param>
    /// <param name="reason">
    /// Optional reason code recorded on the audit row when
    /// <paramref name="enabled"/> is false. Must be one of
    /// <see cref="ValidReasons.All"/>; ignored when <paramref name="enabled"/>
    /// is true.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// <see cref="Result{T, TError}"/> with <see cref="Unit"/> on success;
    /// otherwise an <see cref="EnableUserError"/> describing the failure.
    /// </returns>
    Task<Result<Unit, EnableUserError>> SetEnabledAsync(
        string upn,
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Force a password reset: write a new <c>unicodePwd</c>, set
    /// <c>pwdLastSet = 0</c> (forcing change at next login), and clear
    /// <c>ACCOUNTDISABLE</c>. The three writes happen in a single
    /// <c>ModifyRequest</c>.
    /// </summary>
    /// <param name="upn">UPN of the user whose password is being reset.</param>
    /// <param name="password">The new password (cleartext, never logged or audited).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// <see cref="Result{T, TError}"/> with <see cref="Unit"/> on success;
    /// otherwise a <see cref="ResetPasswordError"/> describing the failure.
    /// </returns>
    /// <exception cref="UserMgmt.Core.Ldap.LdapsRequiredException">
    /// Thrown when the bound connection is not on port 636.
    /// </exception>
    Task<Result<Unit, ResetPasswordError>> ResetPasswordAsync(
        string upn,
        string password,
        CancellationToken cancellationToken = default);
}
