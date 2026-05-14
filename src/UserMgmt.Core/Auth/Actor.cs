namespace UserMgmt.Core.Auth;

/// <summary>
/// Identifies who performed an action across the system.
/// Returned by <see cref="ICurrentActor"/> and stamped onto every audit row.
/// </summary>
/// <param name="Upn">The UPN of the actor (consistent across Web, Api, and System surfaces).</param>
/// <param name="Source">The surface the actor came from.</param>
public sealed record Actor(string Upn, ActorSource Source)
{
    /// <summary>
    /// True if the actor is a non-human surface — ML training jobs and background services.
    /// Useful for filtering training inputs and for audit-log triage.
    /// </summary>
    public bool IsSystem => Source is ActorSource.System or ActorSource.MLRetrain;
}
