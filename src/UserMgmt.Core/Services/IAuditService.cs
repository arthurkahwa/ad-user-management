using UserMgmt.Core.Common;

namespace UserMgmt.Core.Services;

/// <summary>
/// Direct audit write surface for actions that do not mutate tracked entities
/// (password reset, group membership). For entity-bound changes the
/// <c>SaveChangesInterceptor</c> on <c>UserMgmtDbContext</c> emits rows
/// automatically — callers must not double-record.
/// </summary>
/// <remarks>
/// The audit row entity is owned by <c>UserMgmt.Data</c>. The interface
/// is typed against <c>object</c> at the seam to keep <c>UserMgmt.Core</c>
/// free of EF Core references; concrete callers in service code use the
/// strongly typed <c>AuditEntryDto</c> below.
/// </remarks>
public interface IAuditService
{
    /// <summary>Record a single audit row.</summary>
    Task RecordAsync(AuditEntryDto entry, CancellationToken cancellationToken = default);

    /// <summary>Page through audit rows targeting a specific UPN, newest first.</summary>
    Task<PagedResult<AuditEntryDto>> QueryForUserAsync(
        string upn,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Wire-shape of an audit row exposed by <see cref="IAuditService"/>.
/// Mirrors the EF entity in <c>UserMgmt.Data</c> without depending on it.
/// </summary>
public sealed record AuditEntryDto(
    long Id,
    DateTime Timestamp,
    string ActorUpn,
    string Action,
    string TargetUpn,
    string FieldName,
    string? OldValue,
    string? NewValue,
    string Source,
    string? Reason);
