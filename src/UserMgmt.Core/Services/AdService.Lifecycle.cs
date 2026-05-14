using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using UserMgmt.Core.Common;
using UserMgmt.Core.Ldap;

namespace UserMgmt.Core.Services;

/// <summary>
/// M1.6 lifecycle operations on <see cref="AdService"/> — flipping
/// <c>ACCOUNTDISABLE</c> in <c>userAccountControl</c> and forcing a password
/// reset. Implemented as a partial class file so the slice can land
/// alongside #5 (Create), #6 (Update), and #8 (Group) without conflict on
/// the read-path file. The first slice to merge promotes
/// <see cref="AdService"/> to <c>partial</c>; the others are a no-op rebase.
/// </summary>
public sealed partial class AdService
{
    private const int AccountDisableBit = 0x2;
    private const int LdapsPort = 636;

    private static readonly Action<ILogger, string, Exception?> LogPasswordResetSucceeded =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(2101, nameof(LogPasswordResetSucceeded)),
            // Password is never interpolated into the format string — only the
            // target UPN, which is not a secret. The log captures the fact of
            // the reset and the principal it touched, never the credential.
            "Password reset issued for '{TargetUpn}'.");

    private static readonly Action<ILogger, string, bool, Exception?> LogSetEnabledSucceeded =
        LoggerMessage.Define<string, bool>(
            LogLevel.Information,
            new EventId(2102, nameof(LogSetEnabledSucceeded)),
            "userAccountControl ACCOUNTDISABLE bit flipped for '{TargetUpn}' (enabled = {Enabled}).");

    // _auditService is declared in AdService.Create.cs (M1.4) — shared across write-path partials.

    /// <summary>
    /// Construct an <see cref="AdService"/> wired up for the M1.6 lifecycle
    /// operations (enable / disable and password reset). The
    /// <see cref="IAuditService"/> is required for these methods so the
    /// audit row can be persisted alongside each AD write.
    /// </summary>
    public AdService(
        IAdConnection connection,
        Microsoft.Extensions.Options.IOptions<AdOptions> options,
        ILogger<AdService> logger,
        IAuditService auditService)
        : this(connection, options, logger)
    {
        ArgumentNullException.ThrowIfNull(auditService);
        _auditService = auditService;
    }

    /// <inheritdoc />
    public async Task<Result<Unit, EnableUserError>> SetEnabledAsync(
        string upn,
        bool enabled,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        // Pre-validate the reason BEFORE any directory traffic. A bad reason
        // would be caught by the CHECK constraint on the audit row anyway,
        // but catching it here keeps the failure on the cheap side of the
        // operation and guarantees we never even try to flip the AD bit
        // on a request the audit log would refuse to record.
        if (!enabled && reason is not null && !ValidReasons.Contains(reason))
        {
            return Result<Unit, EnableUserError>.Failure(new EnableUserError.InvalidReason(reason));
        }

        // Read current state. We need both the DN (for the ModifyRequest
        // target) and the raw userAccountControl string (for the delete-old
        // half of the CAS).
        (string? dn, string? oldUacRaw) = await GetDnAndUacAsync(upn, cancellationToken).ConfigureAwait(false);
        if (dn is null || oldUacRaw is null)
        {
            return Result<Unit, EnableUserError>.Failure(new EnableUserError.UserNotFound(upn));
        }

        if (!int.TryParse(oldUacRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int oldUac))
        {
            // A non-integer userAccountControl is server corruption, not a
            // concurrency issue. Surface as conflict so the caller renders
            // a recovery banner rather than crashing — the user can retry
            // and we'll re-read.
            return Result<Unit, EnableUserError>.Failure(
                new EnableUserError.ConcurrencyConflict(AdAttributes.UserAccountControl, oldUacRaw));
        }

        int newUac = enabled
            ? oldUac & ~AccountDisableBit
            : oldUac | AccountDisableBit;
        string newUacRaw = newUac.ToString(CultureInfo.InvariantCulture);

        // Attribute-level CAS via delete-old / add-new on userAccountControl.
        // MS-ADTS §3.1.1.5.3.3 — DirectoryServer rejects the modification
        // when the supplied "old" value does not equal the server's current
        // value, giving us optimistic concurrency without an exclusive lock.
        ModifyRequest request = new(dn);
        request.Modifications.Add(new DirectoryAttributeModification
        {
            Name = AdAttributes.UserAccountControl,
            Operation = DirectoryAttributeOperation.Delete,
        });
        request.Modifications[0].Add(oldUacRaw);

        request.Modifications.Add(new DirectoryAttributeModification
        {
            Name = AdAttributes.UserAccountControl,
            Operation = DirectoryAttributeOperation.Add,
        });
        request.Modifications[1].Add(newUacRaw);

        try
        {
            await _connection.ModifyAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (IsCasFailure(ex))
        {
            // Re-read to surface the current value to the caller so the UI
            // can render "another writer changed this; current state is X".
            (_, string? currentUacRaw) = await GetDnAndUacAsync(upn, cancellationToken).ConfigureAwait(false);
            return Result<Unit, EnableUserError>.Failure(
                new EnableUserError.ConcurrencyConflict(AdAttributes.UserAccountControl, currentUacRaw));
        }

        LogSetEnabledSucceeded(_logger, upn, enabled, null);

        await RecordEnableAuditAsync(upn, enabled, oldUacRaw, newUacRaw, reason, cancellationToken)
            .ConfigureAwait(false);

        return Result<Unit, EnableUserError>.Success(Unit.Value);
    }

    /// <inheritdoc />
    public async Task<Result<Unit, ResetPasswordError>> ResetPasswordAsync(
        string upn,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        // The password parameter is never logged below. ArgumentException on
        // a blank password would not interpolate it, but we still check
        // before reaching the directory layer.
        ArgumentException.ThrowIfNullOrEmpty(password);

        // Hard precondition: password-sensitive writes only over LDAPS.
        // Surfaced as an exception (not an error union member) because
        // running a password write against plain LDAP would be a
        // deployment-level mistake the caller should not silently absorb.
        if (_connection.Port != LdapsPort)
        {
            throw new LdapsRequiredException();
        }

        (string? dn, string? oldUacRaw) = await GetDnAndUacAsync(upn, cancellationToken).ConfigureAwait(false);
        if (dn is null)
        {
            return Result<Unit, ResetPasswordError>.Failure(new ResetPasswordError.UserNotFound(upn));
        }

        // Clear ACCOUNTDISABLE so the password reset implicitly re-enables a
        // dormant / locked account in the same RPC. When the server returns
        // no userAccountControl value (extremely unusual but tolerated),
        // default to the well-known NORMAL_ACCOUNT (512) — clearing the bit
        // on an already-clear value is a no-op, never a violation.
        int oldUac = 512;
        if (oldUacRaw is not null
            && int.TryParse(oldUacRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            oldUac = parsed;
        }

        int newUac = oldUac & ~AccountDisableBit;
        string newUacRaw = newUac.ToString(CultureInfo.InvariantCulture);

        // AD requires unicodePwd to be the new password wrapped in U+0022
        // double-quote characters and encoded as UTF-16LE — see
        // MS-ADTS §3.1.1.3.1.5. We compute the encoded bytes once here,
        // submit them in the ModifyRequest, and never log either the
        // cleartext or the encoded form.
        byte[] encodedPassword = Encoding.Unicode.GetBytes($"\"{password}\"");

        ModifyRequest request = new(dn);

        DirectoryAttributeModification pwdMod = new()
        {
            Name = AdAttributes.UnicodePwd,
            Operation = DirectoryAttributeOperation.Replace,
        };
        pwdMod.Add(encodedPassword);
        request.Modifications.Add(pwdMod);

        DirectoryAttributeModification pwdLastSetMod = new()
        {
            Name = AdAttributes.PwdLastSet,
            Operation = DirectoryAttributeOperation.Replace,
        };
        pwdLastSetMod.Add("0");
        request.Modifications.Add(pwdLastSetMod);

        DirectoryAttributeModification uacMod = new()
        {
            Name = AdAttributes.UserAccountControl,
            Operation = DirectoryAttributeOperation.Replace,
        };
        uacMod.Add(newUacRaw);
        request.Modifications.Add(uacMod);

        await _connection.ModifyAsync(request, cancellationToken).ConfigureAwait(false);

        LogPasswordResetSucceeded(_logger, upn, null);

        await RecordPasswordResetAuditAsync(upn, cancellationToken).ConfigureAwait(false);

        return Result<Unit, ResetPasswordError>.Success(Unit.Value);
    }

    /// <summary>
    /// Look up the user's DN and current <c>userAccountControl</c> value in a
    /// single search. Returns <c>(null, null)</c> when no entry matches.
    /// </summary>
    private async Task<(string? Dn, string? UserAccountControlRaw)> GetDnAndUacAsync(
        string upn,
        CancellationToken cancellationToken)
    {
        string filter = $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={LdapFilterEscape.Escape(upn)}))";

        SearchRequest request = new(
            _options.BaseDn,
            filter,
            SearchScope.Subtree,
            AdAttributes.DistinguishedName,
            AdAttributes.UserAccountControl);

        SearchResponse response;
        try
        {
            response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            return (null, null);
        }

        if (response.Entries.Count == 0)
        {
            return (null, null);
        }

        SearchResultEntry entry = response.Entries[0];
        string dn = entry.DistinguishedName ?? string.Empty;

        string? uacRaw = null;
        if (entry.Attributes.Contains(AdAttributes.UserAccountControl))
        {
            DirectoryAttribute attr = entry.Attributes[AdAttributes.UserAccountControl];
            if (attr.Count > 0)
            {
                object? value = attr[0];
                uacRaw = value switch
                {
                    string s => s,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => value?.ToString(),
                };
            }
        }

        return (dn, uacRaw);
    }

    /// <summary>
    /// Decide whether an <see cref="DirectoryOperationException"/> represents
    /// a CAS failure on the delete-old / add-new pair (the server's view of
    /// the attribute does not match our "old" value).
    /// </summary>
    private static bool IsCasFailure(DirectoryOperationException ex)
    {
        // AD returns NoSuchAttribute when delete-old's value does not exist
        // (the attribute drifted between read and write), and
        // AttributeOrValueExists when add-new's value is already there.
        // Either one indicates the same condition: the server's state is
        // not what we just read.
        ResultCode? code = ex.Response?.ResultCode;
        return code is ResultCode.NoSuchAttribute or ResultCode.AttributeOrValueExists;
    }

    private async Task RecordEnableAuditAsync(
        string upn,
        bool enabled,
        string oldUacRaw,
        string newUacRaw,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (_auditService is null)
        {
            return;
        }

        AuditEntryDto dto = new(
            Id: 0,
            Timestamp: default,
            ActorUpn: string.Empty, // overwritten by AuditService from ICurrentActor
            Action: enabled ? "Enable" : "Disable",
            TargetUpn: upn,
            FieldName: AdAttributes.UserAccountControl,
            OldValue: oldUacRaw,
            NewValue: newUacRaw,
            Source: string.Empty, // overwritten by AuditService from ICurrentActor
            Reason: enabled ? null : reason);

        await _auditService.RecordAsync(dto, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordPasswordResetAuditAsync(string upn, CancellationToken cancellationToken)
    {
        if (_auditService is null)
        {
            return;
        }

        // OldValue / NewValue are intentionally null. The password is a secret
        // and must never appear in the audit log; the existence of the row
        // (action=ResetPassword, target=upn) is the audit signal.
        AuditEntryDto dto = new(
            Id: 0,
            Timestamp: default,
            ActorUpn: string.Empty,
            Action: "ResetPassword",
            TargetUpn: upn,
            FieldName: AdAttributes.UnicodePwd,
            OldValue: null,
            NewValue: null,
            Source: string.Empty,
            Reason: null);

        await _auditService.RecordAsync(dto, cancellationToken).ConfigureAwait(false);
    }
}

// AD attribute name constants used by lifecycle methods (UnicodePwd, PwdLastSet)
// are declared in AdService.Create.cs (M1.4) — shared across write-path partials.
