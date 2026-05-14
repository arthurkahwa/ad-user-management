namespace UserMgmt.Core.Auth;

/// <summary>
/// A fixed <see cref="Actor"/> instance for background services that have no
/// HTTP context — reconciliation worker, ML trainer, etc.
/// </summary>
public static class SystemActor
{
    /// <summary>The shared System-source actor used by background hosts.</summary>
    public static Actor Instance { get; } = new("system@local", ActorSource.System);
}
