using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Common;
using UserMgmt.Core.Ldap;

namespace UserMgmt.Core.Services;

/// <summary>
/// Group-membership write path for <see cref="AdService"/> (M1.7).
/// </summary>
/// <remarks>
/// Mutations target the group object's <c>member</c> attribute — AD treats
/// <c>memberOf</c> on the user object as a computed back-link, so the
/// canonical write path is on the group object. Idempotency outcomes
/// (<see cref="AlreadyMember"/> / <see cref="NotAMember"/>) are surfaced as
/// typed <see cref="GroupMembershipError"/> cases instead of unwrapped LDAP
/// exceptions, mirroring the spec in <c>docs/ARCHITECTURE-NOTES.md</c>.
/// </remarks>
public sealed partial class AdService
{
    private const string MemberAttribute = "member";

    private static readonly string[] GroupAttributes = [MemberAttribute];

    private static readonly string[] UserDnOnlyAttributes = [AdAttributes.DistinguishedName];

    // _auditService is declared in AdService.Create.cs (M1.4) — shared across all write-path partials.
    private readonly ICurrentActor? _currentActor;

    /// <summary>
    /// Construct an <see cref="AdService"/> with the audit + actor dependencies
    /// required for group-membership writes.
    /// </summary>
    /// <remarks>
    /// The M1.2 read-path constructor remains for back-compat; callers that
    /// only exercise read paths (<see cref="SearchAsync"/> /
    /// <see cref="GetAsync"/>) need not supply an audit service. Write paths
    /// added in later slices will validate non-null at the call site.
    /// </remarks>
    public AdService(
        IAdConnection connection,
        Microsoft.Extensions.Options.IOptions<AdOptions> options,
        ILogger<AdService> logger,
        IAuditService auditService,
        ICurrentActor currentActor)
        : this(connection, options, logger)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(currentActor);
        _auditService = auditService;
        _currentActor = currentActor;
    }

    /// <inheritdoc />
    public async Task<Result<Unit, GroupMembershipError>> AddToGroupAsync(
        string upn,
        string groupDn,
        CancellationToken cancellationToken = default)
    {
        ValidateMembershipArguments(upn, groupDn);

        string? userDn = await ResolveUserDnAsync(upn, cancellationToken).ConfigureAwait(false);
        if (userDn is null)
        {
            return Result<Unit, GroupMembershipError>.Failure(new UserNotFound(upn));
        }

        SearchResultEntry? groupEntry = await ResolveGroupAsync(groupDn, cancellationToken).ConfigureAwait(false);
        if (groupEntry is null)
        {
            return Result<Unit, GroupMembershipError>.Failure(new GroupNotFound(groupDn));
        }

        if (GroupContainsMember(groupEntry, userDn))
        {
            return Result<Unit, GroupMembershipError>.Failure(new AlreadyMember(upn, groupDn));
        }

        ModifyRequest request = new(
            groupDn,
            DirectoryAttributeOperation.Add,
            MemberAttribute,
            userDn);

        await _connection.ModifyAsync(request, cancellationToken).ConfigureAwait(false);

        await RecordMembershipAuditAsync(
            action: "AddToGroup",
            targetUpn: upn,
            oldValue: null,
            newValue: groupDn,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<Unit, GroupMembershipError>.Success(Unit.Value);
    }

    /// <inheritdoc />
    public async Task<Result<Unit, GroupMembershipError>> RemoveFromGroupAsync(
        string upn,
        string groupDn,
        CancellationToken cancellationToken = default)
    {
        ValidateMembershipArguments(upn, groupDn);

        string? userDn = await ResolveUserDnAsync(upn, cancellationToken).ConfigureAwait(false);
        if (userDn is null)
        {
            return Result<Unit, GroupMembershipError>.Failure(new UserNotFound(upn));
        }

        SearchResultEntry? groupEntry = await ResolveGroupAsync(groupDn, cancellationToken).ConfigureAwait(false);
        if (groupEntry is null)
        {
            return Result<Unit, GroupMembershipError>.Failure(new GroupNotFound(groupDn));
        }

        if (!GroupContainsMember(groupEntry, userDn))
        {
            return Result<Unit, GroupMembershipError>.Failure(new NotAMember(upn, groupDn));
        }

        ModifyRequest request = new(
            groupDn,
            DirectoryAttributeOperation.Delete,
            MemberAttribute,
            userDn);

        await _connection.ModifyAsync(request, cancellationToken).ConfigureAwait(false);

        await RecordMembershipAuditAsync(
            action: "RemoveFromGroup",
            targetUpn: upn,
            oldValue: groupDn,
            newValue: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return Result<Unit, GroupMembershipError>.Success(Unit.Value);
    }

    private static void ValidateMembershipArguments(string upn, string groupDn)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        if (string.IsNullOrWhiteSpace(groupDn))
        {
            throw new ArgumentException("Group DN must not be empty.", nameof(groupDn));
        }
    }

    private async Task<string?> ResolveUserDnAsync(string upn, CancellationToken cancellationToken)
    {
        string filter = $"(userPrincipalName={LdapFilterEscape.Escape(upn)})";

        SearchRequest request = new(
            _options.BaseDn,
            filter,
            SearchScope.Subtree,
            UserDnOnlyAttributes);

        SearchResponse response;
        try
        {
            response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            return null;
        }

        if (response.Entries.Count == 0)
        {
            return null;
        }

        SearchResultEntry entry = response.Entries[0];
        string dn = entry.DistinguishedName ?? string.Empty;
        return string.IsNullOrEmpty(dn) ? null : dn;
    }

    private async Task<SearchResultEntry?> ResolveGroupAsync(string groupDn, CancellationToken cancellationToken)
    {
        string filter = $"(distinguishedName={LdapFilterEscape.Escape(groupDn)})";

        SearchRequest request = new(
            groupDn,
            filter,
            SearchScope.Base,
            GroupAttributes);

        SearchResponse response;
        try
        {
            response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            return null;
        }

        if (response.Entries.Count == 0)
        {
            return null;
        }

        return response.Entries[0];
    }

    private static bool GroupContainsMember(SearchResultEntry groupEntry, string userDn)
    {
        SearchResultAttributeCollection attrs = groupEntry.Attributes;
        if (!attrs.Contains(MemberAttribute))
        {
            return false;
        }

        DirectoryAttribute memberAttr = attrs[MemberAttribute];
        for (int i = 0; i < memberAttr.Count; i++)
        {
            object? value = memberAttr[i];
            string? existing = value switch
            {
                string s => s,
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                _ => value?.ToString(),
            };

            if (existing is not null && string.Equals(existing, userDn, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task RecordMembershipAuditAsync(
        string action,
        string targetUpn,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken)
    {
        // Group-membership operations bypass the SaveChanges interceptor — the
        // mutation hits AD, not the DbContext — so the audit row is emitted
        // directly through IAuditService.
        if (_auditService is null)
        {
            throw new InvalidOperationException(
                "AdService was constructed without IAuditService; group-membership writes require the audit-aware constructor.");
        }

        AuditEntryDto dto = new(
            Id: 0,
            Timestamp: default,
            ActorUpn: string.Empty, // overwritten by AuditService from ICurrentActor
            Action: action,
            TargetUpn: targetUpn,
            FieldName: MemberAttribute,
            OldValue: oldValue,
            NewValue: newValue,
            Source: string.Empty, // overwritten by AuditService from ICurrentActor
            Reason: null);

        await _auditService.RecordAsync(dto, cancellationToken).ConfigureAwait(false);
    }
}
