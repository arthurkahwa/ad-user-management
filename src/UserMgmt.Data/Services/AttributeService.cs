using Microsoft.EntityFrameworkCore;
using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Services;

namespace UserMgmt.Data.Services;

/// <summary>
/// Default <see cref="IAttributeService"/> backed by <see cref="UserMgmtDbContext"/>.
/// </summary>
/// <remarks>
/// Lives in <c>UserMgmt.Data</c> for the same reason
/// <see cref="AuditService"/> does: the implementation depends on
/// <see cref="UserMgmtDbContext"/>, which lives in <c>UserMgmt.Data</c>.
/// The interface stays in <c>UserMgmt.Core</c> so callers depend on the
/// abstraction only.
/// <para>
/// Audit rows for entity changes are emitted by
/// <see cref="UserMgmt.Data.Interceptors.AuditSaveChangesInterceptor"/>
/// — this service must not call <see cref="IAuditService.RecordAsync"/>
/// directly or rows will be double-recorded.
/// </para>
/// </remarks>
public sealed class AttributeService : IAttributeService
{
    private readonly UserMgmtDbContext _db;

    /// <summary>Create a new service.</summary>
    public AttributeService(UserMgmtDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <inheritdoc />
    public async Task<UserAttributes?> GetAsync(string upn, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        return await _db.UserAttributes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Upn == upn, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Result<UserAttributes, ConcurrencyConflict>> UpsertAsync(
        string upn,
        UserAttributesDto dto,
        Guid? ifMatchRowVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        ArgumentNullException.ThrowIfNull(dto);

        var existing = await _db.UserAttributes
            .SingleOrDefaultAsync(e => e.Upn == upn, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // Insert path: caller should not have a prior RowVersion to match.
            // If they supplied one, treat it as a concurrency conflict — the
            // row they expected to be updating does not exist.
            if (ifMatchRowVersion is not null)
            {
                return Result<UserAttributes, ConcurrencyConflict>.Failure(
                    new ConcurrencyConflict(nameof(UserAttributes.RowVersion), CurrentValue: null));
            }

            var inserted = new UserAttributes
            {
                Upn = upn,
                CostCenter = dto.CostCenter,
                ContractType = dto.ContractType,
                EmployeeId = dto.EmployeeId,
                RowVersion = Guid.NewGuid(),
            };

            _db.UserAttributes.Add(inserted);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Result<UserAttributes, ConcurrencyConflict>.Success(inserted);
        }

        // Update path: caller must supply a RowVersion that matches the current
        // row, otherwise we're racing another writer.
        if (ifMatchRowVersion is null || existing.RowVersion != ifMatchRowVersion.Value)
        {
            return Result<UserAttributes, ConcurrencyConflict>.Failure(
                new ConcurrencyConflict(nameof(UserAttributes.RowVersion), existing.RowVersion.ToString()));
        }

        existing.CostCenter = dto.CostCenter;
        existing.ContractType = dto.ContractType;
        existing.EmployeeId = dto.EmployeeId;
        existing.RowVersion = Guid.NewGuid();

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Race between the in-memory check above and the SQL commit: another
            // writer committed in between. Refresh and surface the conflict.
            var current = await _db.UserAttributes
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.Upn == upn, cancellationToken)
                .ConfigureAwait(false);
            return Result<UserAttributes, ConcurrencyConflict>.Failure(
                new ConcurrencyConflict(nameof(UserAttributes.RowVersion), current?.RowVersion.ToString()));
        }

        return Result<UserAttributes, ConcurrencyConflict>.Success(existing);
    }

    /// <inheritdoc />
    public async Task<Result<UserAttributes, ConcurrencyConflict>> SetExcludeFromMlAsync(
        string upn,
        bool excluded,
        Guid ifMatchRowVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        var existing = await _db.UserAttributes
            .SingleOrDefaultAsync(e => e.Upn == upn, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null || existing.RowVersion != ifMatchRowVersion)
        {
            return Result<UserAttributes, ConcurrencyConflict>.Failure(
                new ConcurrencyConflict(nameof(UserAttributes.RowVersion), existing?.RowVersion.ToString()));
        }

        existing.ExcludeFromMLScoring = excluded;
        existing.RowVersion = Guid.NewGuid();

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await _db.UserAttributes
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.Upn == upn, cancellationToken)
                .ConfigureAwait(false);
            return Result<UserAttributes, ConcurrencyConflict>.Failure(
                new ConcurrencyConflict(nameof(UserAttributes.RowVersion), current?.RowVersion.ToString()));
        }

        return Result<UserAttributes, ConcurrencyConflict>.Success(existing);
    }
}
