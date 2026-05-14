namespace UserMgmt.Core.Common;

/// <summary>
/// Tagged-union root for failure outcomes of
/// <c>AdService.AddToGroupAsync</c> / <c>AdService.RemoveFromGroupAsync</c>.
/// </summary>
/// <remarks>
/// Idempotency outcomes (<see cref="AlreadyMember"/> / <see cref="NotAMember"/>)
/// are modelled as typed failures rather than thrown exceptions so callers can
/// branch on intent without unwrapping LDAP error codes. The
/// <see cref="UserNotFound"/> case is the existing flat record in
/// <c>Errors.cs</c>; it derives from this base so the M1.7 union can reuse it
/// without duplication.
/// </remarks>
public abstract record GroupMembershipError;

/// <summary>The group DN supplied to the membership operation has no matching group object.</summary>
/// <param name="GroupDn">The distinguished name that was queried.</param>
public sealed record GroupNotFound(string GroupDn) : GroupMembershipError;

/// <summary>
/// The user is already a member of the group; <c>AddToGroupAsync</c> returns this
/// rather than issuing a no-op <c>ModifyRequest</c>.
/// </summary>
/// <param name="Upn">The UPN being added.</param>
/// <param name="GroupDn">The group distinguished name.</param>
public sealed record AlreadyMember(string Upn, string GroupDn) : GroupMembershipError;

/// <summary>
/// The user is not a member of the group; <c>RemoveFromGroupAsync</c> returns this
/// rather than issuing a no-op <c>ModifyRequest</c>.
/// </summary>
/// <param name="Upn">The UPN being removed.</param>
/// <param name="GroupDn">The group distinguished name.</param>
public sealed record NotAMember(string Upn, string GroupDn) : GroupMembershipError;
