using Microsoft.EntityFrameworkCore;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Common;
using UserMgmt.Core.Services;
using UserMgmt.Data.Entities;

namespace UserMgmt.Data.Services;

/// <summary>
/// Default <see cref="IAuditService"/> backed by <see cref="UserMgmtDbContext"/>.
/// </summary>
/// <remarks>
/// Lives in <c>UserMgmt.Data</c> rather than <c>UserMgmt.Core/Services/</c>
/// because it depends on <see cref="UserMgmtDbContext"/>. The interface
/// <see cref="IAuditService"/> remains in <c>UserMgmt.Core</c> so consumers
/// take a dependency on the abstraction only.
/// </remarks>
public sealed class AuditService : IAuditService
{
    private readonly UserMgmtDbContext _db;
    private readonly ICurrentActor _currentActor;
    private readonly TimeProvider _timeProvider;

    /// <summary>Create a new service.</summary>
    public AuditService(
        UserMgmtDbContext db,
        ICurrentActor currentActor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(currentActor);
        _db = db;
        _currentActor = currentActor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditEntryDto entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var actor = _currentActor.Current;

        var row = new AuditEntry
        {
            Timestamp = entry.Timestamp == default ? _timeProvider.GetUtcNow().UtcDateTime : entry.Timestamp,
            // Always stamp the actor identity from ICurrentActor rather than from the
            // caller-supplied DTO: callers should not be able to forge actor UPN.
            ActorUpn = actor.Upn,
            Action = entry.Action,
            TargetUpn = entry.TargetUpn,
            FieldName = entry.FieldName,
            OldValue = entry.OldValue,
            NewValue = entry.NewValue,
            Source = actor.Source.ToString(),
            Reason = entry.Reason,
        };

        _db.AuditEntries.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<AuditEntryDto>> QueryForUserAsync(
        string upn,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be 1 or greater.");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "PageSize must be 1 or greater.");
        }

        var query = _db.AuditEntries.AsNoTracking().Where(e => e.TargetUpn == upn);

        int totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var rows = await query
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new AuditEntryDto(
                e.Id,
                e.Timestamp,
                e.ActorUpn,
                e.Action,
                e.TargetUpn,
                e.FieldName,
                e.OldValue,
                e.NewValue,
                e.Source,
                e.Reason))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PagedResult<AuditEntryDto>(rows, page, pageSize, totalCount);
    }
}
