namespace UserMgmt.Core.Auth;

/// <summary>
/// Surfaces the current actor identity to the service layer without
/// leaking ASP.NET Core or background-host types into the core domain.
/// </summary>
/// <remarks>
/// HTTP-context-backed implementations (cookie + Kerberos for M2, JWT for M3)
/// live in their respective host projects. The core ships only the
/// abstraction and a <see cref="SystemActor"/> for background services.
/// </remarks>
public interface ICurrentActor
{
    /// <summary>The actor currently flowing through the call stack.</summary>
    Actor Current { get; }
}
