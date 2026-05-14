using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;

namespace UserMgmt.Core.Services;

/// <summary>
/// AD-side surface for the user management application.
/// </summary>
/// <remarks>
/// M1.2 ships only the read operations (<see cref="SearchAsync"/> and
/// <see cref="GetAsync"/>). Write operations (<c>CreateAsync</c>,
/// <c>UpdateAsync</c>, <c>SetEnabledAsync</c>, <c>ResetPasswordAsync</c>,
/// and group membership) land in subsequent M1 slices and will extend this
/// interface in additive partial files or revisions.
/// </remarks>
public interface IAdService
{
    /// <summary>
    /// Page through users whose display attributes match the supplied query.
    /// </summary>
    /// <param name="query">
    /// Free-form text that matches on <c>cn</c>, <c>displayName</c>,
    /// <c>sAMAccountName</c>, or <c>userPrincipalName</c>. The value is RFC
    /// 4515-escaped before being concatenated into a filter.
    /// </param>
    /// <param name="page">1-based page index.</param>
    /// <param name="pageSize">Maximum items per page.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>A page of users with paging metadata.</returns>
    Task<PagedResult<AdUser>> SearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Look up a single user by UPN.
    /// </summary>
    /// <param name="upn">The user-principal-name to fetch.</param>
    /// <param name="cancellationToken">Token to cancel the in-flight request.</param>
    /// <returns>The user, or null when no AD object matches the UPN.</returns>
    Task<AdUser?> GetAsync(string upn, CancellationToken cancellationToken = default);
}
