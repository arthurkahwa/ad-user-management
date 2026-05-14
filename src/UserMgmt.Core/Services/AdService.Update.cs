using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging;
using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Ldap;

namespace UserMgmt.Core.Services;

// Implementation note: this file is the M1.5 update-path slice. The whole
// purpose of the LDAP attribute-level CAS implemented here is to avoid the
// AD timestamp attribute that the README calls out as informational only —
// it is intentionally never read in this file. A unit test
// (AdServiceUpdateTests.UpdateAsync_DoesNotReadWhenChanged_VerifiedByFileGrep)
// grep-asserts that the literal name of that attribute does not appear in
// this source.
public sealed partial class AdService
{
    /// <summary>
    /// Attribute name → mutation route. Drives the dispatch in
    /// <see cref="UpdateAsync"/>: AD attributes flow through
    /// <see cref="IAdConnection.ModifyAsync"/>; sidecar attributes flow
    /// through <see cref="IAttributeService.UpsertAsync"/>. Keys are
    /// matched case-insensitively to match LDAP attribute conventions and
    /// the PascalCase used for the sidecar DTO fields.
    /// </summary>
    private static readonly Dictionary<string, AttributeRoute> AttributeRoutes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // AD attributes — whitelisted set per the M1.5 issue body.
            [AdAttributes.DisplayName] = AttributeRoute.ActiveDirectory,
            [AdAttributes.GivenName] = AttributeRoute.ActiveDirectory,
            [AdAttributes.Surname] = AttributeRoute.ActiveDirectory,
            [AdAttributes.Department] = AttributeRoute.ActiveDirectory,
            [AdAttributes.Manager] = AttributeRoute.ActiveDirectory,
            ["mail"] = AttributeRoute.ActiveDirectory,
            ["telephoneNumber"] = AttributeRoute.ActiveDirectory,

            // Sidecar attributes — persisted in UserMgmt.Data.
            [SidecarAttributes.CostCenter] = AttributeRoute.Sidecar,
            [SidecarAttributes.ContractType] = AttributeRoute.Sidecar,
            [SidecarAttributes.EmployeeId] = AttributeRoute.Sidecar,
        };

    private static readonly Action<ILogger, string, string, Exception?> LogAdAttributeConflict =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(2101, nameof(LogAdAttributeConflict)),
            "LDAP attribute-level CAS failed for UPN '{Upn}', attribute '{Attribute}' — Delete-old did not match the server's current value.");

    // _attributeService and _auditService are declared in AdService.Create.cs (M1.4) — shared across write-path partials.

    /// <summary>
    /// Extended constructor that wires the sidecar and audit collaborators
    /// required by <see cref="UpdateAsync"/>. DI registrations supply this
    /// overload; tests targeting the read path can keep using the original
    /// (connection, options, logger) constructor.
    /// </summary>
    public AdService(
        IAdConnection connection,
        Microsoft.Extensions.Options.IOptions<Ldap.AdOptions> options,
        ILogger<AdService> logger,
        IAttributeService attributeService,
        IAuditService auditService)
        : this(connection, options, logger)
    {
        ArgumentNullException.ThrowIfNull(attributeService);
        ArgumentNullException.ThrowIfNull(auditService);
        _attributeService = attributeService;
        _auditService = auditService;
    }

    /// <inheritdoc />
    public async Task<Result<Unit, UpdateUserError>> UpdateAsync(
        string upn,
        IReadOnlyDictionary<string, string?> changes,
        IReadOnlyDictionary<string, string?> ifMatchAttributes,
        Guid? ifMatchSidecarToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(ifMatchAttributes);

        // Empty change-set is a no-op success — callers should not have to
        // special-case it themselves.
        if (changes.Count == 0)
        {
            return Result<Unit, UpdateUserError>.Success(Unit.Value);
        }

        // Up-front validation: reject unknown attributes before any side
        // effect. This keeps the "first conflict stops processing" contract
        // honest — unknown keys must not silently slip through after some
        // valid attributes have already been applied.
        foreach (string key in changes.Keys)
        {
            if (!AttributeRoutes.ContainsKey(key))
            {
                return Result<Unit, UpdateUserError>.Failure(new UpdateUserError.UnknownAttribute(key));
            }
        }

        bool hasAdChange = changes.Any(kvp => AttributeRoutes[kvp.Key] == AttributeRoute.ActiveDirectory);
        bool hasSidecarChange = changes.Any(kvp => AttributeRoutes[kvp.Key] == AttributeRoute.Sidecar);

        // We need the target DN to address ModifyRequest. Skip the AD GetAsync
        // when no AD change is being applied — keeps a pure-sidecar update
        // from making an LDAP round-trip.
        AdUser? user = null;
        if (hasAdChange)
        {
            user = await GetAsync(upn, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return Result<Unit, UpdateUserError>.Failure(
                    new UpdateUserError.NotFound(new UserNotFound(upn)));
            }
        }

        // Apply AD changes first, one ModifyRequest per attribute. On the
        // first conflict, stop — earlier successful modifies stay applied.
        // This is the documented eventual-consistency contract for this
        // slice; callers reading the README cross-store consistency section
        // know to surface the partial state to the operator.
        foreach (var kvp in changes)
        {
            if (AttributeRoutes[kvp.Key] != AttributeRoute.ActiveDirectory)
            {
                continue;
            }

            if (!ifMatchAttributes.TryGetValue(kvp.Key, out string? ifMatchValue))
            {
                // No prior-value supplied for an AD attribute is itself a
                // CAS failure — we have no way to construct the Delete half.
                return Result<Unit, UpdateUserError>.Failure(
                    new UpdateUserError.Concurrency(new ConcurrencyConflict(kvp.Key, CurrentValue: null)));
            }

            Result<Unit, UpdateUserError> modifyResult = await ApplyAdAttributeChangeAsync(
                upn,
                user!.Dn,
                kvp.Key,
                oldValue: ifMatchValue,
                newValue: kvp.Value,
                cancellationToken).ConfigureAwait(false);

            if (!modifyResult.IsSuccess)
            {
                return modifyResult;
            }
        }

        // Sidecar changes route to AttributeService.UpsertAsync. The
        // UpsertAsync upsert semantics naturally cover the "row may not yet
        // exist" case; we surface its ConcurrencyConflict as our own.
        if (hasSidecarChange)
        {
            if (_attributeService is null)
            {
                throw new InvalidOperationException(
                    "AdService was constructed without an IAttributeService; sidecar updates are not available. Use the constructor overload that takes IAttributeService and IAuditService.");
            }

            UserAttributesDto dto = BuildSidecarDto(changes);

            var upsertResult = await _attributeService
                .UpsertAsync(upn, dto, ifMatchSidecarToken, cancellationToken)
                .ConfigureAwait(false);

            if (!upsertResult.IsSuccess)
            {
                return Result<Unit, UpdateUserError>.Failure(
                    new UpdateUserError.Concurrency(upsertResult.Error!));
            }
        }

        return Result<Unit, UpdateUserError>.Success(Unit.Value);
    }

    /// <summary>
    /// Build the sidecar DTO from the change-set. Sidecar keys that aren't
    /// present in <paramref name="changes"/> are sent as <c>null</c> — but
    /// because the underlying <see cref="IAttributeService.UpsertAsync"/>
    /// always writes all three fields, callers must include the keys they
    /// want preserved with their existing value. We document this in the
    /// PR; for now the dispatcher does the simplest correct thing.
    /// </summary>
    private static UserAttributesDto BuildSidecarDto(IReadOnlyDictionary<string, string?> changes)
    {
        string? costCenter = changes.TryGetValue(SidecarAttributes.CostCenter, out string? cc) ? cc : null;
        string? contractType = changes.TryGetValue(SidecarAttributes.ContractType, out string? ct) ? ct : null;
        string? employeeId = changes.TryGetValue(SidecarAttributes.EmployeeId, out string? eid) ? eid : null;
        return new UserAttributesDto(costCenter, contractType, employeeId);
    }

    /// <summary>
    /// Issue a single <see cref="ModifyRequest"/> carrying a Delete-old and
    /// (optionally) an Add-new <see cref="DirectoryAttributeModification"/>
    /// against the target DN. The Delete fails with
    /// <see cref="ResultCode.NoSuchAttribute"/> when <paramref name="oldValue"/>
    /// does not match the server's current attribute value — that path
    /// surfaces as <see cref="UpdateUserError.Concurrency"/>.
    /// </summary>
    private async Task<Result<Unit, UpdateUserError>> ApplyAdAttributeChangeAsync(
        string upn,
        string dn,
        string attributeName,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken)
    {
        ModifyRequest request = new(dn);

        // Delete the old value. If oldValue is null we cannot build a
        // valued-Delete (the server semantics require the literal value to
        // delete); treat it as a CAS failure. In practice the caller should
        // have read the row before calling Update, so a null oldValue means
        // "no attribute was set previously" — and there's no Delete to
        // issue. We model this as: emit only the Add half. This is a
        // documented interpretation call; see the PR description.
        if (oldValue is not null)
        {
            request.Modifications.Add(new DirectoryAttributeModification
            {
                Name = attributeName,
                Operation = DirectoryAttributeOperation.Delete,
            });
            request.Modifications[^1].Add(oldValue);
        }

        if (newValue is not null)
        {
            request.Modifications.Add(new DirectoryAttributeModification
            {
                Name = attributeName,
                Operation = DirectoryAttributeOperation.Add,
            });
            request.Modifications[^1].Add(newValue);
        }

        if (request.Modifications.Count == 0)
        {
            // Nothing to delete, nothing to add — no-op success.
            return Result<Unit, UpdateUserError>.Success(Unit.Value);
        }

        try
        {
            _ = await _connection.ModifyAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (IsCasMiss(ex))
        {
            LogAdAttributeConflict(_logger, upn, attributeName, ex);
            string? currentValue = await ReadCurrentValueAsync(dn, attributeName, cancellationToken)
                .ConfigureAwait(false);
            return Result<Unit, UpdateUserError>.Failure(
                new UpdateUserError.Concurrency(new ConcurrencyConflict(attributeName, currentValue)));
        }

        // Audit row for the AD change. The interceptor on UserMgmtDbContext
        // only observes EF-tracked entity changes — AD writes do not flow
        // through SaveChanges, so we must record the row directly here.
        // (Sidecar changes are caught by the interceptor; we deliberately
        // do not also call RecordAsync for those, to avoid double-auditing.)
        if (_auditService is not null)
        {
            await _auditService.RecordAsync(
                new AuditEntryDto(
                    Id: 0,
                    Timestamp: default,
                    ActorUpn: string.Empty,
                    Action: "Update",
                    TargetUpn: upn,
                    FieldName: attributeName,
                    OldValue: oldValue,
                    NewValue: newValue,
                    Source: string.Empty,
                    Reason: null),
                cancellationToken).ConfigureAwait(false);
        }

        return Result<Unit, UpdateUserError>.Success(Unit.Value);
    }

    /// <summary>
    /// True when the LDAP server's response indicates the Delete-old half
    /// of the CAS pair did not match the current attribute value. The
    /// canonical result code is <see cref="ResultCode.NoSuchAttribute"/>;
    /// some servers also surface <c>AttributeOrValueExists</c> for the
    /// add-then-delete combination depending on order — we treat both as
    /// CAS misses since either way the in-memory and server states have
    /// diverged.
    /// </summary>
    private static bool IsCasMiss(DirectoryOperationException ex)
    {
        if (ex.Response is null)
        {
            return false;
        }

        return ex.Response.ResultCode switch
        {
            ResultCode.NoSuchAttribute => true,
            ResultCode.AttributeOrValueExists => true,
            _ => false,
        };
    }

    /// <summary>
    /// Best-effort read-back of the current value of <paramref name="attributeName"/>
    /// from <paramref name="dn"/>, so the surfaced <see cref="ConcurrencyConflict"/>
    /// can carry it. Failures during read-back fall back to <c>null</c>: the
    /// conflict is reported either way.
    /// </summary>
    private async Task<string?> ReadCurrentValueAsync(string dn, string attributeName, CancellationToken cancellationToken)
    {
        try
        {
            SearchRequest request = new(
                dn,
                "(objectClass=*)",
                SearchScope.Base,
                [attributeName]);

            SearchResponse response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.Entries.Count == 0)
            {
                return null;
            }

            SearchResultEntry entry = response.Entries[0];
            if (!entry.Attributes.Contains(attributeName))
            {
                return null;
            }

            DirectoryAttribute attr = entry.Attributes[attributeName];
            if (attr.Count == 0)
            {
                return null;
            }

            return attr[0] switch
            {
                string s => s,
                byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                object o => o.ToString(),
                null => null,
            };
        }
#pragma warning disable CA1031 // Best-effort read-back: any failure surfaces as null current value.
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Discriminator for the dispatch table.
    /// </summary>
    private enum AttributeRoute
    {
        ActiveDirectory,
        Sidecar,
    }

    /// <summary>Canonical sidecar attribute names matching the property names on <see cref="UserAttributes"/>.</summary>
    internal static class SidecarAttributes
    {
        public const string CostCenter = nameof(UserAttributes.CostCenter);
        public const string ContractType = nameof(UserAttributes.ContractType);
        public const string EmployeeId = nameof(UserAttributes.EmployeeId);
    }
}
