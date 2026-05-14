namespace UserMgmt.Core.Common;

/// <summary>
/// Surfaces a concurrency conflict — either an LDAP attribute-level CAS failure
/// (AD's "delete-old, add-new" pattern) or a SQL <c>RowVersion</c> mismatch.
/// Both surfaces use the same record so callers handle them uniformly.
/// </summary>
/// <param name="Attribute">The attribute / column whose state has drifted.</param>
/// <param name="CurrentValue">The value currently present on the server, if known.</param>
public sealed record ConcurrencyConflict(string Attribute, string? CurrentValue);

/// <summary>The requested operation targets an OU outside the configured whitelist.</summary>
/// <param name="OuPath">The OU distinguished name the caller supplied.</param>
public sealed record OuNotAllowed(string OuPath);

/// <summary>A user with this UPN already exists in AD.</summary>
/// <param name="Upn">The conflicting UPN.</param>
public sealed record UpnAlreadyExists(string Upn);

/// <summary>No user matches the supplied UPN.</summary>
/// <param name="Upn">The UPN that was queried.</param>
public sealed record UserNotFound(string Upn);

/// <summary>
/// Indicates a cross-store write that succeeded in AD but failed in the sidecar.
/// The caller should render a high-visibility warning and surface the
/// reconciliation queue entry. See README §Cross-store consistency.
/// </summary>
/// <param name="Value">The value created in AD (so callers can still render it).</param>
/// <param name="SidecarFailureReason">A human-readable failure description.</param>
public sealed record PartialSuccess<T>(T Value, string SidecarFailureReason);
