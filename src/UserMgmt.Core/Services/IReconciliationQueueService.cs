namespace UserMgmt.Core.Services;

/// <summary>
/// Write-only surface for the partial-state reconciliation queue.
/// </summary>
/// <remarks>
/// The queue entity lives in <c>UserMgmt.Data.Entities.ReconciliationQueue</c>;
/// the interface stays in <c>UserMgmt.Core</c> so service-layer code (which
/// must enqueue rows on cross-store failures) doesn't take a dependency on
/// <c>UserMgmt.Data</c>. M1 ships only enqueue — the queue-processing hosted
/// service and admin-resolve operations belong to a later milestone, so the
/// shape here is deliberately narrow.
/// </remarks>
public interface IReconciliationQueueService
{
    /// <summary>
    /// Enqueue a new partial-state row.
    /// </summary>
    /// <param name="targetUpn">UPN of the user the partial state affects.</param>
    /// <param name="operation">Operation tag (e.g. <c>CreateUser-SidecarMissing</c>).</param>
    /// <param name="payload">JSON payload with the original attempt details.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task EnqueueAsync(
        string targetUpn,
        string operation,
        string payload,
        CancellationToken cancellationToken = default);
}
