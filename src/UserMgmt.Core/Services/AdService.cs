using System.DirectoryServices.Protocols;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Ldap;

namespace UserMgmt.Core.Services;

/// <summary>
/// Default <see cref="IAdService"/> implementation over <see cref="IAdConnection"/>.
/// </summary>
/// <remarks>
/// M1.2 covers only the read path (<see cref="SearchAsync"/>,
/// <see cref="GetAsync"/>). Filter inputs are routed through
/// <see cref="LdapFilterEscape.Escape"/> before string concatenation; AD
/// attribute reads use the attribute set documented in
/// <c>docs/ARCHITECTURE-NOTES.md</c> (<see cref="AdUser"/> shape).
/// </remarks>
public sealed partial class AdService : IAdService
{
    private const string ObjectClassFilter = "(&(objectCategory=person)(objectClass=user)";

    private static readonly string[] DefaultAttributes =
    [
        AdAttributes.SamAccountName,
        AdAttributes.UserPrincipalName,
        AdAttributes.DistinguishedName,
        AdAttributes.DisplayName,
        AdAttributes.GivenName,
        AdAttributes.Surname,
        AdAttributes.Department,
        AdAttributes.Manager,
        AdAttributes.WhenCreated,
        AdAttributes.LastLogonTimestamp,
        AdAttributes.UserAccountControl,
    ];

    private static readonly Action<ILogger, string, Exception?> LogUpnLookupNoSuchObject =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(2001, nameof(LogUpnLookupNoSuchObject)),
            "AD search for UPN '{Upn}' returned NoSuchObject.");

    private readonly IAdConnection _connection;
    private readonly AdOptions _options;
    private readonly ILogger<AdService> _logger;

    /// <summary>Create a new service.</summary>
    public AdService(IAdConnection connection, IOptions<AdOptions> options, ILogger<AdService> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdUser>> SearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Page must be 1 or greater.");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "PageSize must be 1 or greater.");
        }

        string filter = BuildSearchFilter(query);
        int skip = (page - 1) * pageSize;
        int collected = 0;
        List<AdUser> pageItems = new(pageSize);
        PageResultRequestControl pageControl = new(pageSize);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SearchRequest request = new(
                _options.BaseDn,
                filter,
                SearchScope.Subtree,
                DefaultAttributes);
            request.Controls.Add(pageControl);

            SearchResponse response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);

            foreach (SearchResultEntry entry in response.Entries)
            {
                int absoluteIndex = collected;
                collected++;

                if (absoluteIndex < skip)
                {
                    continue;
                }

                if (pageItems.Count < pageSize)
                {
                    pageItems.Add(MapEntry(entry));
                }
                // Continue counting past the page to populate TotalCount.
            }

            // Pull the response cookie out and decide whether to continue.
            byte[]? cookie = null;
            foreach (DirectoryControl control in response.Controls)
            {
                if (control is PageResultResponseControl pageResponse)
                {
                    cookie = pageResponse.Cookie;
                    break;
                }
            }

            if (cookie is null || cookie.Length == 0)
            {
                break;
            }

            pageControl.Cookie = cookie;
        }

        return new PagedResult<AdUser>(pageItems, page, pageSize, collected);
    }

    /// <inheritdoc />
    public async Task<AdUser?> GetAsync(string upn, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            throw new ArgumentException("UPN must not be empty.", nameof(upn));
        }

        string filter = $"(&(objectCategory=person)(objectClass=user)(userPrincipalName={LdapFilterEscape.Escape(upn)}))";

        SearchRequest request = new(
            _options.BaseDn,
            filter,
            SearchScope.Subtree,
            DefaultAttributes);

        SearchResponse response;
        try
        {
            response = await _connection.SearchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryOperationException ex) when (ex.Response is { ResultCode: ResultCode.NoSuchObject })
        {
            LogUpnLookupNoSuchObject(_logger, upn, ex);
            return null;
        }

        if (response.Entries.Count == 0)
        {
            return null;
        }

        return MapEntry(response.Entries[0]);
    }

    /// <summary>
    /// Compose the filter used by <see cref="SearchAsync"/>. Every
    /// user-supplied fragment is escaped through
    /// <see cref="LdapFilterEscape.Escape"/> before concatenation.
    /// </summary>
    private static string BuildSearchFilter(string rawQuery)
    {
        if (string.IsNullOrEmpty(rawQuery))
        {
            return ObjectClassFilter + ")";
        }

        string escaped = LdapFilterEscape.Escape(rawQuery);

        // Match across the typical user-visible fields. A single wildcard at
        // either end gives a "contains" match without exposing the user's raw
        // text as a wildcard fragment.
        string orClause =
            $"(|(cn=*{escaped}*)" +
            $"(displayName=*{escaped}*)" +
            $"(sAMAccountName=*{escaped}*)" +
            $"(userPrincipalName=*{escaped}*))";

        return ObjectClassFilter + orClause + ")";
    }

    /// <summary>
    /// Project a <see cref="SearchResultEntry"/> into the public
    /// <see cref="AdUser"/> shape, including the computed Enabled flag and
    /// the lastLogonTimestamp → DateTime conversion.
    /// </summary>
    internal static AdUser MapEntry(SearchResultEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        SearchResultAttributeCollection attrs = entry.Attributes;
        string dn = entry.DistinguishedName ?? string.Empty;

        string sam = GetSingleString(attrs, AdAttributes.SamAccountName) ?? string.Empty;
        string upn = GetSingleString(attrs, AdAttributes.UserPrincipalName) ?? string.Empty;
        string display = GetSingleString(attrs, AdAttributes.DisplayName) ?? string.Empty;
        string? givenName = GetSingleString(attrs, AdAttributes.GivenName);
        string? surname = GetSingleString(attrs, AdAttributes.Surname);
        string? department = GetSingleString(attrs, AdAttributes.Department);
        string? manager = GetSingleString(attrs, AdAttributes.Manager);
        string? whenCreatedRaw = GetSingleString(attrs, AdAttributes.WhenCreated);
        string? lastLogonRaw = GetSingleString(attrs, AdAttributes.LastLogonTimestamp);
        string? uacRaw = GetSingleString(attrs, AdAttributes.UserAccountControl);

        DateTime whenCreated = ParseGeneralizedTime(whenCreatedRaw);
        DateTime? lastLogon = ParseFileTime(lastLogonRaw);
        bool enabled = !HasAccountDisableBit(uacRaw);
        string ouPath = ExtractOuPath(dn);

        return new AdUser(
            Upn: upn,
            SamAccountName: sam,
            Dn: dn,
            DisplayName: display,
            GivenName: givenName,
            Surname: surname,
            Department: department,
            ManagerDn: manager,
            OuPath: ouPath,
            WhenCreated: whenCreated,
            LastLogon: lastLogon,
            Enabled: enabled);
    }

    private static string? GetSingleString(SearchResultAttributeCollection attrs, string name)
    {
        if (!attrs.Contains(name))
        {
            return null;
        }

        DirectoryAttribute attr = attrs[name];
        if (attr.Count == 0)
        {
            return null;
        }

        object? value = attr[0];
        return value switch
        {
            string s => s,
            byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
            _ => value?.ToString(),
        };
    }

    private static DateTime ParseGeneralizedTime(string? raw)
    {
        // AD "whenCreated" is in generalized-time format, e.g.
        // "20260513094500.0Z". Trim the fractional segment and parse as UTC.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return default;
        }

        const string Format = "yyyyMMddHHmmss";
        string trimmed = raw;
        int dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            trimmed = trimmed[..dot];
        }
        else if (trimmed.EndsWith('Z'))
        {
            trimmed = trimmed[..^1];
        }

        if (DateTime.TryParseExact(
                trimmed,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return default;
    }

    private static DateTime? ParseFileTime(string? raw)
    {
        // lastLogonTimestamp is a Windows FILETIME (100-ns ticks since
        // 1601-01-01 UTC). Zero or "never logged on" sentinel → null.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
        {
            return null;
        }

        if (ticks <= 0)
        {
            return null;
        }

        try
        {
            return DateTime.FromFileTimeUtc(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool HasAccountDisableBit(string? uacRaw)
    {
        // userAccountControl: bit 0x2 (ACCOUNTDISABLE) — see MS-ADTS.
        if (string.IsNullOrWhiteSpace(uacRaw))
        {
            return false;
        }

        if (!int.TryParse(uacRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int uac))
        {
            return false;
        }

        return (uac & 0x2) != 0;
    }

    private static string ExtractOuPath(string dn)
    {
        // "CN=Alice,OU=Sales,OU=Corp,DC=example,DC=com" → "OU=Sales,OU=Corp,DC=example,DC=com"
        if (string.IsNullOrWhiteSpace(dn))
        {
            return string.Empty;
        }

        int firstComma = dn.IndexOf(',', StringComparison.Ordinal);
        return firstComma < 0 ? dn : dn[(firstComma + 1)..];
    }
}

/// <summary>Canonical AD attribute names referenced by <see cref="AdService"/>.</summary>
internal static class AdAttributes
{
    public const string SamAccountName = "sAMAccountName";
    public const string UserPrincipalName = "userPrincipalName";
    public const string DistinguishedName = "distinguishedName";
    public const string DisplayName = "displayName";
    public const string GivenName = "givenName";
    public const string Surname = "sn";
    public const string Department = "department";
    public const string Manager = "manager";
    public const string WhenCreated = "whenCreated";
    public const string LastLogonTimestamp = "lastLogonTimestamp";
    public const string UserAccountControl = "userAccountControl";
}
