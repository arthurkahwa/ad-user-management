using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;

namespace UserMgmt.Core.Services;

/// <summary>
/// CRUD surface for <see cref="UserAttributes"/> — the sidecar attributes
/// (cost centre, contract type, employee ID, ML-exclude flag) that AD does not
/// store. Optimistic concurrency uses the row's <c>RowVersion</c> token;
/// conflicts surface as a typed <see cref="ConcurrencyConflict"/> rather than
/// an EF Core exception so callers can render the right banner without
/// parsing exception text.
/// </summary>
/// <remarks>
/// Audit rows for upserts and the ML-exclude flip are emitted automatically
/// by the <c>AuditSaveChangesInterceptor</c> on the <c>UserMgmtDbContext</c>
/// — implementations must not call <see cref="IAuditService.RecordAsync"/>
/// for entity-bound changes.
/// </remarks>
public interface IAttributeService
{
    /// <summary>
    /// Fetch the sidecar attributes for a UPN, or <c>null</c> if no row exists.
    /// </summary>
    Task<UserAttributes?> GetAsync(string upn, CancellationToken cancellationToken = default);

    /// <summary>
    /// Insert or update the sidecar attributes for <paramref name="upn"/>.
    /// </summary>
    /// <param name="upn">UPN — also the primary key.</param>
    /// <param name="dto">The writable fields. <c>ExcludeFromMLScoring</c> is not in this DTO; mutate it through <see cref="SetExcludeFromMlAsync"/>.</param>
    /// <param name="ifMatchRowVersion">
    /// The <c>RowVersion</c> the caller last observed. <c>null</c> when the
    /// caller is inserting a new row (no prior version to match). Non-null
    /// values that don't match the row's current <c>RowVersion</c> surface as
    /// <see cref="ConcurrencyConflict"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result<UserAttributes, ConcurrencyConflict>> UpsertAsync(
        string upn,
        UserAttributesDto dto,
        Guid? ifMatchRowVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Toggle the <c>ExcludeFromMLScoring</c> flag.
    /// </summary>
    /// <param name="upn">UPN — also the primary key.</param>
    /// <param name="excluded">Target value of the flag.</param>
    /// <param name="ifMatchRowVersion">The <c>RowVersion</c> the caller last observed; must match the current value or the call returns <see cref="ConcurrencyConflict"/>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<Result<UserAttributes, ConcurrencyConflict>> SetExcludeFromMlAsync(
        string upn,
        bool excluded,
        Guid ifMatchRowVersion,
        CancellationToken cancellationToken = default);
}
