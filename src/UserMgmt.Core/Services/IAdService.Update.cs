using UserMgmt.Core.Common;

namespace UserMgmt.Core.Services;

/// <summary>
/// Update surface of <see cref="IAdService"/> — landed by M1.5.
/// </summary>
/// <remarks>
/// Declared as a separate <c>partial</c> file so M1 write-path slices (#5,
/// #6, #7, #8) can each extend the interface without conflicting on a single
/// member list.
/// </remarks>
public partial interface IAdService
{
    /// <summary>
    /// Apply a set of attribute changes to the user identified by <paramref name="upn"/>
    /// using the only properly atomic concurrency primitive AD offers:
    /// per-attribute <c>delete-old-value</c> + <c>add-new-value</c> in a single
    /// <c>ModifyRequest</c>. Sidecar fields (<c>CostCenter</c>, <c>ContractType</c>,
    /// <c>EmployeeId</c>) route to <see cref="IAttributeService.UpsertAsync"/> and
    /// use the SQL <c>RowVersion</c> token instead.
    /// </summary>
    /// <param name="upn">UPN of the target user.</param>
    /// <param name="changes">
    /// New values keyed by attribute name. A null value clears the attribute.
    /// Whitelisted AD attributes: <c>displayName</c>, <c>givenName</c>, <c>sn</c>,
    /// <c>department</c>, <c>manager</c>, <c>mail</c>, <c>telephoneNumber</c>.
    /// Sidecar attributes: <c>CostCenter</c>, <c>ContractType</c>, <c>EmployeeId</c>.
    /// Any other key surfaces as <see cref="UpdateUserError.UnknownAttribute"/>.
    /// </param>
    /// <param name="ifMatchAttributes">
    /// Previous values keyed by attribute name. Required for every AD attribute in
    /// <paramref name="changes"/>; the <c>Delete</c> half of the CAS uses this
    /// value, so a drift between <paramref name="ifMatchAttributes"/> and the
    /// current attribute value surfaces as a typed
    /// <see cref="UpdateUserError.Concurrency"/>.
    /// </param>
    /// <param name="ifMatchSidecarToken">
    /// The <c>RowVersion</c> the caller last observed on the sidecar row. May be
    /// null only when no sidecar attribute is being updated.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// <see cref="Unit"/> on success. On the first conflict the call stops and
    /// returns <see cref="UpdateUserError.Concurrency"/> naming the offending
    /// attribute — earlier successful AD modifies stay applied (documented
    /// eventual-consistency contract for this slice).
    /// </returns>
    Task<Result<Unit, UpdateUserError>> UpdateAsync(
        string upn,
        IReadOnlyDictionary<string, string?> changes,
        IReadOnlyDictionary<string, string?> ifMatchAttributes,
        Guid? ifMatchSidecarToken,
        CancellationToken cancellationToken = default);
}
