using UserMgmt.Core.Domain;

namespace UserMgmt.Core.Common;

/// <summary>
/// Tagged-union failure type for <c>IAdService.CreateAsync</c>.
/// </summary>
/// <remarks>
/// Represented as a sealed-record base with sealed-record cases so the
/// compiler can exhaustively check <c>switch</c> projections. The three
/// cases mirror the failure modes described in the M1.4 issue and in
/// README §Cross-store consistency:
/// <list type="bullet">
///   <item><description><see cref="OuNotAllowed"/> — pre-flight whitelist rejection; no AD interaction occurred.</description></item>
///   <item><description><see cref="UpnAlreadyExists"/> — the UPN already exists in AD; no AD write was attempted.</description></item>
///   <item><description><see cref="PartialSuccess"/> — AD-side create succeeded but the sidecar write failed; the created <see cref="AdUser"/> is carried so callers can render a warning banner with the user details.</description></item>
/// </list>
/// </remarks>
public abstract record CreateUserError
{
    private CreateUserError()
    {
    }

    /// <summary>The supplied OU is not in <c>AdOptions.AllowedOus</c>.</summary>
    /// <param name="OuPath">The OU distinguished name the caller supplied.</param>
    public sealed record OuNotAllowed(string OuPath) : CreateUserError;

    /// <summary>A user with this UPN already exists in AD.</summary>
    /// <param name="Upn">The conflicting UPN.</param>
    public sealed record UpnAlreadyExists(string Upn) : CreateUserError;

    /// <summary>
    /// AD create succeeded but the sidecar write failed. The caller should
    /// render a high-visibility warning banner; a <c>ReconciliationQueue</c>
    /// row has already been enqueued for admin resolution. This case is
    /// modelled as a failure (not a success) so callers must explicitly
    /// acknowledge the partial state instead of treating the create as
    /// fully successful.
    /// </summary>
    /// <param name="User">The user that was created in AD.</param>
    /// <param name="SidecarFailureReason">Human-readable description of the sidecar failure. Never contains the password.</param>
    public sealed record PartialSuccess(AdUser User, string SidecarFailureReason) : CreateUserError;
}
