using System.Diagnostics.CodeAnalysis;

namespace UserMgmt.Core.Common;

/// <summary>
/// Discriminated union of failures the AD update path can surface.
/// Each leaf is a sealed record inheriting from this abstract base, so
/// callers can <c>switch</c> exhaustively on the concrete type.
/// </summary>
/// <remarks>
/// <para>
/// Reuses <see cref="ConcurrencyConflict"/> (M1.1) for both AD attribute-level
/// CAS failures and SQL <c>RowVersion</c> mismatches — the wire shape is the
/// same and the <c>Attribute</c> field names the field whose state drifted.
/// </para>
/// <para>
/// <see cref="UserNotFound"/> is reused identically.
/// </para>
/// </remarks>
public abstract record UpdateUserError
{
    /// <summary>An AD attribute or sidecar <c>RowVersion</c> CAS failed.</summary>
    /// <param name="Conflict">The underlying concurrency conflict (attribute name and current value).</param>
    public sealed record Concurrency(ConcurrencyConflict Conflict) : UpdateUserError;

    /// <summary>No user matches the supplied UPN.</summary>
    /// <param name="Detail">The not-found marker carrying the UPN.</param>
    public sealed record NotFound(UserNotFound Detail) : UpdateUserError;

    /// <summary>
    /// The caller supplied a key in the <c>changes</c> dictionary that is neither
    /// a whitelisted AD attribute nor a recognised sidecar field.
    /// </summary>
    /// <param name="Name">The unknown attribute name.</param>
    [SuppressMessage(
        "Naming",
        "CA1711:Identifiers should not have incorrect suffix",
        Justification = "The name is required by the M1.5 issue acceptance criteria — the failure case is about an unknown LDAP/sidecar attribute, and 'Attribute' is the domain term used throughout the slice.")]
    public sealed record UnknownAttribute(string Name) : UpdateUserError;
}
