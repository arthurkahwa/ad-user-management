namespace UserMgmt.Core.Auth;

/// <summary>
/// Identifies the surface that produced an audited action.
/// Used in audit rows and by ML training to exclude system-generated noise.
/// </summary>
public enum ActorSource
{
    /// <summary>Razor Pages web UI (cookie + Kerberos auth).</summary>
    Web,

    /// <summary>REST API surface (JWT bearer auth).</summary>
    Api,

    /// <summary>ML.NET retraining job — excluded from ML feature inputs.</summary>
    MLRetrain,

    /// <summary>Background services and other in-process system actors.</summary>
    System,
}
