using UserMgmt.Core.Services;
using UserMgmt.Data.Entities;

namespace UserMgmt.Data.Services;

/// <summary>
/// Default <see cref="IReconciliationQueueService"/> backed by <see cref="UserMgmtDbContext"/>.
/// </summary>
/// <remarks>
/// Lives in <c>UserMgmt.Data</c> alongside <see cref="AttributeService"/>
/// and <see cref="AuditService"/> for the same reason: the implementation
/// depends on <see cref="UserMgmtDbContext"/>. M1.4 uses this to flag the
/// AD-created / sidecar-missing partial state from
/// <c>AdService.CreateAsync</c>.
/// </remarks>
public sealed class ReconciliationQueueService : IReconciliationQueueService
{
    private readonly UserMgmtDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>Create a new service.</summary>
    public ReconciliationQueueService(UserMgmtDbContext db, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(
        string targetUpn,
        string operation,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetUpn))
        {
            throw new ArgumentException("Target UPN must not be empty.", nameof(targetUpn));
        }

        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("Operation must not be empty.", nameof(operation));
        }

        ArgumentNullException.ThrowIfNull(payload);

        var row = new ReconciliationQueue
        {
            Timestamp = _timeProvider.GetUtcNow().UtcDateTime,
            TargetUpn = targetUpn,
            Operation = operation,
            Payload = payload,
            Status = ReconciliationStatus.Open,
        };

        _db.ReconciliationQueue.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
