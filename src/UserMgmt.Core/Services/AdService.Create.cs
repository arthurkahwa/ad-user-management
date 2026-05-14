using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Ldap;

namespace UserMgmt.Core.Services;

/// <summary>
/// M1.4: <c>CreateAsync</c> implementation for <see cref="AdService"/>.
/// </summary>
/// <remarks>
/// Kept in its own partial-class file so the four sibling M1 slices
/// (Update, Enable/Reset, Group +/-, Create) can land in parallel without
/// touching each other's code. The Create-only dependencies
/// (<see cref="IAttributeService"/>, <see cref="IAuditService"/>,
/// <see cref="IReconciliationQueueService"/>) are injected through a
/// dedicated overload of the primary constructor; the read-only ctor on
/// <see cref="AdService"/> still works for callers that only need
/// <c>SearchAsync</c> / <c>GetAsync</c>.
/// </remarks>
public sealed partial class AdService
{
    /// <summary>
    /// userAccountControl flag value for a normal, enabled account
    /// (NORMAL_ACCOUNT, ACCOUNTDISABLE cleared). MS-ADTS §2.2.16.
    /// </summary>
    private const string UacNormalEnabled = "512";

    private static readonly Action<ILogger, string, Exception?> LogCreateOuNotAllowed =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2101, nameof(LogCreateOuNotAllowed)),
            "CreateAsync rejected: OU '{OuPath}' is not in AdOptions.AllowedOus.");

    private static readonly Action<ILogger, string, Exception?> LogCreateUpnExists =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2102, nameof(LogCreateUpnExists)),
            "CreateAsync rejected: UPN '{Upn}' already exists in AD.");

    private static readonly Action<ILogger, string, Exception?> LogCreateSucceeded =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2103, nameof(LogCreateSucceeded)),
            "CreateAsync succeeded for UPN '{Upn}'; AD object and sidecar row written.");

    private static readonly Action<ILogger, string, string, Exception?> LogCreatePartialState =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2104, nameof(LogCreatePartialState)),
            "CreateAsync partial state for UPN '{Upn}': AD object created, sidecar failed ({Reason}). Reconciliation row enqueued.");

    private readonly IAttributeService? _attributeService;
    private readonly IAuditService? _auditService;
    private readonly IReconciliationQueueService? _reconciliationQueue;

    /// <summary>
    /// Write-path constructor used by M1.4+. Adds the sidecar, audit, and
    /// reconciliation-queue dependencies that the cross-store
    /// <see cref="CreateAsync"/> needs. Read-only callers can keep using
    /// the 3-arg overload defined in <c>AdService.cs</c>.
    /// </summary>
    public AdService(
        IAdConnection connection,
        IOptions<AdOptions> options,
        ILogger<AdService> logger,
        IAttributeService attributeService,
        IAuditService auditService,
        IReconciliationQueueService reconciliationQueue)
        : this(connection, options, logger)
    {
        ArgumentNullException.ThrowIfNull(attributeService);
        ArgumentNullException.ThrowIfNull(auditService);
        ArgumentNullException.ThrowIfNull(reconciliationQueue);

        _attributeService = attributeService;
        _auditService = auditService;
        _reconciliationQueue = reconciliationQueue;
    }

    /// <inheritdoc />
    public async Task<Result<AdUser, CreateUserError>> CreateAsync(
        NewUserDto dto,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        // Password is intentionally NOT validated for emptiness here so a
        // null/empty value surfaces as an LDAP-side error rather than an
        // exception message that might be logged with the parameter name.
        ArgumentNullException.ThrowIfNull(password);

        if (_attributeService is null || _auditService is null || _reconciliationQueue is null)
        {
            throw new InvalidOperationException(
                "AdService.CreateAsync requires the write-path constructor (IAttributeService, IAuditService, IReconciliationQueueService).");
        }

        // 1. OU whitelist — bail out before any AD interaction. README §Cross-store consistency:
        //    "AD-first, SQL-second" only kicks in once we've decided to write.
        if (!IsOuWhitelisted(dto.OuPath))
        {
            LogCreateOuNotAllowed(_logger, dto.OuPath, null);
            return Result<AdUser, CreateUserError>.Failure(new CreateUserError.OuNotAllowed(dto.OuPath));
        }

        // 2. LDAPS enforcement — the connection must be on 636 before any
        //    password-touching operation. IAdConnection.Port surfaces this
        //    without leaking LdapConnection internals.
        if (_connection.Port != 636)
        {
            throw new LdapsRequiredException();
        }

        // 3. Uniqueness check via LDAP search with the UPN
        //    RFC 4515-escaped. A hit means we must NOT issue AddRequest.
        if (await UpnExistsAsync(dto.Upn, cancellationToken).ConfigureAwait(false))
        {
            LogCreateUpnExists(_logger, dto.Upn, null);
            return Result<AdUser, CreateUserError>.Failure(new CreateUserError.UpnAlreadyExists(dto.Upn));
        }

        // 4. AD operations: AddRequest, then ModifyRequest setting unicodePwd,
        //    pwdLastSet, and userAccountControl. Two round-trips because AD
        //    cannot set unicodePwd in the same operation that creates the
        //    object — the password attribute requires the object to exist
        //    first. We collapse the password / pwdLastSet / UAC writes into
        //    a single ModifyRequest so AD only sees one modify round-trip
        //    after creation. Any failure here bubbles as an
        //    LDAP/DirectoryOperationException without any password content.
        string dn = BuildDistinguishedName(dto);
        AddRequest addRequest = BuildAddRequest(dn, dto);
        await _connection.AddAsync(addRequest, cancellationToken).ConfigureAwait(false);

        ModifyRequest modifyRequest = BuildPasswordAndUacModifyRequest(dn, password);
        await _connection.ModifyAsync(modifyRequest, cancellationToken).ConfigureAwait(false);

        // 5. Project the in-memory dto into an AdUser. We deliberately do NOT
        //    re-read the object from AD here — the read-after-write would add
        //    a third round-trip for cosmetics. WhenCreated is stamped with
        //    UtcNow as a near-truth approximation; subsequent IAdService.GetAsync
        //    calls will surface the canonical AD-stamped value.
        AdUser adUser = ProjectNewUser(dto, dn);

        // 6. Sidecar upsert. Failures here are partial state — see
        //    README §Cross-store consistency. We never auto-roll-back AD.
        UserAttributesDto sidecarDto = new(dto.CostCenter, dto.ContractType, dto.EmployeeId);

        try
        {
            var sidecarResult = await _attributeService
                .UpsertAsync(dto.Upn, sidecarDto, ifMatchRowVersion: null, cancellationToken)
                .ConfigureAwait(false);

            if (!sidecarResult.IsSuccess)
            {
                string reason = sidecarResult.Error is { } err
                    ? $"Sidecar concurrency conflict on '{err.Attribute}' (current: {err.CurrentValue ?? "<none>"})."
                    : "Sidecar upsert returned a failure result.";
                await HandlePartialStateAsync(adUser, sidecarDto, reason, cancellationToken)
                    .ConfigureAwait(false);
                return Result<AdUser, CreateUserError>.Failure(
                    new CreateUserError.PartialSuccess(adUser, reason));
            }
        }
#pragma warning disable CA1031 // Catch-all on the sidecar failure path is deliberate: partial-state must capture every transport / constraint / runtime failure. The exception type itself goes into the log; only the message goes into the audit / reconciliation payload, and never the password.
        catch (Exception ex)
        {
            string reason = $"Sidecar upsert threw {ex.GetType().Name}: {ex.Message}";
            await HandlePartialStateAsync(adUser, sidecarDto, reason, cancellationToken)
                .ConfigureAwait(false);
            return Result<AdUser, CreateUserError>.Failure(
                new CreateUserError.PartialSuccess(adUser, reason));
        }
#pragma warning restore CA1031

        LogCreateSucceeded(_logger, dto.Upn, null);
        return Result<AdUser, CreateUserError>.Success(adUser);
    }

    private bool IsOuWhitelisted(string ouPath)
    {
        if (_options.AllowedOus.Count == 0)
        {
            return false;
        }

        foreach (string allowed in _options.AllowedOus)
        {
            if (string.Equals(allowed, ouPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> UpnExistsAsync(string upn, CancellationToken cancellationToken)
    {
        string filter = $"(userPrincipalName={LdapFilterEscape.Escape(upn)})";
        SearchRequest request = new(
            _options.BaseDn,
            filter,
            SearchScope.Subtree,
            AdAttributes.UserPrincipalName);

        SearchResponse response;
        try
        {
            response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            return false;
        }

        return response.Entries.Count > 0;
    }

    private static string BuildDistinguishedName(NewUserDto dto) =>
        // CN component is the displayName per AD convention. The displayName
        // is NOT RFC 4515-escaped here because the DN is going into AddRequest's
        // DistinguishedName slot, not into a search filter; the BCL handles
        // DN escaping for us via the LdapConnection layer.
        $"CN={dto.DisplayName},{dto.OuPath}";

    private static AddRequest BuildAddRequest(string dn, NewUserDto dto)
    {
        AddRequest request = new(dn);
        request.Attributes.Add(new DirectoryAttribute(
            AdAttributes.ObjectClass,
            ["top", "person", "organizationalPerson", "user"]));
        request.Attributes.Add(new DirectoryAttribute(AdAttributes.Cn, dto.DisplayName));
        request.Attributes.Add(new DirectoryAttribute(AdAttributes.SamAccountName, dto.SamAccountName));
        request.Attributes.Add(new DirectoryAttribute(AdAttributes.UserPrincipalName, dto.Upn));
        request.Attributes.Add(new DirectoryAttribute(AdAttributes.DisplayName, dto.DisplayName));
        request.Attributes.Add(new DirectoryAttribute(AdAttributes.GivenName, dto.GivenName));
        request.Attributes.Add(new DirectoryAttribute(AdAttributes.Surname, dto.Surname));

        if (!string.IsNullOrWhiteSpace(dto.Department))
        {
            request.Attributes.Add(new DirectoryAttribute(AdAttributes.Department, dto.Department));
        }

        if (!string.IsNullOrWhiteSpace(dto.ManagerDn))
        {
            request.Attributes.Add(new DirectoryAttribute(AdAttributes.Manager, dto.ManagerDn));
        }

        return request;
    }

    private static ModifyRequest BuildPasswordAndUacModifyRequest(string dn, string password)
    {
        // AD requires the password to be set as a UTF-16LE-encoded
        // quoted string in the unicodePwd attribute. The quotes are part
        // of the wire format, not display. pwdLastSet=0 forces the user
        // to change the password on first logon. userAccountControl=512
        // marks the account NORMAL_ACCOUNT with ACCOUNTDISABLE cleared.
        // All three live in one ModifyRequest so the password / UAC writes
        // hit AD atomically from the application's point of view.
        byte[] unicodePwd = Encoding.Unicode.GetBytes($"\"{password}\"");

        DirectoryAttributeModification pwdMod = new()
        {
            Name = AdAttributes.UnicodePwd,
            Operation = DirectoryAttributeOperation.Replace,
        };
        pwdMod.Add(unicodePwd);

        DirectoryAttributeModification pwdLastSetMod = new()
        {
            Name = AdAttributes.PwdLastSet,
            Operation = DirectoryAttributeOperation.Replace,
        };
        pwdLastSetMod.Add("0");

        DirectoryAttributeModification uacMod = new()
        {
            Name = AdAttributes.UserAccountControl,
            Operation = DirectoryAttributeOperation.Replace,
        };
        uacMod.Add(UacNormalEnabled);

        ModifyRequest request = new(dn);
        request.Modifications.Add(pwdMod);
        request.Modifications.Add(pwdLastSetMod);
        request.Modifications.Add(uacMod);
        return request;
    }

    private static AdUser ProjectNewUser(NewUserDto dto, string dn) =>
        new(
            Upn: dto.Upn,
            SamAccountName: dto.SamAccountName,
            Dn: dn,
            DisplayName: dto.DisplayName,
            GivenName: dto.GivenName,
            Surname: dto.Surname,
            Department: dto.Department,
            ManagerDn: dto.ManagerDn,
            OuPath: dto.OuPath,
            WhenCreated: DateTime.UtcNow,
            LastLogon: null,
            Enabled: true);

    private async Task HandlePartialStateAsync(
        AdUser adUser,
        UserAttributesDto sidecarDto,
        string reason,
        CancellationToken cancellationToken)
    {
        // The payload carries ONLY the sidecar fields and the target UPN —
        // never the password, never the AD object's raw attribute set. The
        // reconciliation worker (out of scope for M1) will use this payload
        // to retry the sidecar insert against the still-existing AD object.
        string payload = JsonSerializer.Serialize(new
        {
            adUser.Upn,
            sidecarDto.CostCenter,
            sidecarDto.ContractType,
            sidecarDto.EmployeeId,
        });

        await _auditService!.RecordAsync(
            new AuditEntryDto(
                Id: 0,
                Timestamp: default,
                ActorUpn: string.Empty,
                Action: "CreateUser-PartialState",
                TargetUpn: adUser.Upn,
                FieldName: "PartialState",
                OldValue: null,
                NewValue: "AD created; sidecar missing",
                Source: string.Empty,
                Reason: null),
            cancellationToken).ConfigureAwait(false);

        await _reconciliationQueue!.EnqueueAsync(
            adUser.Upn,
            "CreateUser-SidecarMissing",
            payload,
            cancellationToken).ConfigureAwait(false);

        LogCreatePartialState(_logger, adUser.Upn, reason, null);
    }
}

/// <summary>
/// Extends the AD attribute constant set used by <see cref="AdService"/>
/// with the write-path attributes referenced in M1.4. Declared
/// <c>partial</c> on the same type so each sibling slice can add its own
/// attribute constants without merge conflicts; the first slice to merge
/// defines the <c>partial</c> keyword on the read-path declaration,
/// subsequent rebases are no-ops.
/// </summary>
internal static partial class AdAttributes
{
    public const string ObjectClass = "objectClass";
    public const string Cn = "cn";
    public const string UnicodePwd = "unicodePwd";
    public const string PwdLastSet = "pwdLastSet";
}
