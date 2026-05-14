namespace UserMgmt.Core.Ldap;

/// <summary>
/// Configuration for the LDAP connection layer.
/// </summary>
/// <remarks>
/// Bound via <c>IOptions&lt;AdOptions&gt;</c>. Defaults assume LDAPS on the
/// standard port; <see cref="PreferredDc"/> is optional and, when null, the
/// runtime DC locator (<c>Domain.GetCurrentDomain().FindDomainController()</c>)
/// is used.
/// </remarks>
public sealed record AdOptions
{
    /// <summary>
    /// Optional override for the DC hostname. When null, the runtime DC
    /// locator selects a controller on first use.
    /// </summary>
    public string? PreferredDc { get; init; }

    /// <summary>
    /// TCP port for LDAP. Defaults to 636 (LDAPS). The service layer reads
    /// this through <c>IAdConnection.Port</c> and rejects password-sensitive
    /// operations when the value is not 636.
    /// </summary>
    public int Port { get; init; } = 636;

    /// <summary>
    /// Base DN for searches (e.g. <c>DC=corp,DC=example,DC=com</c>). The
    /// service layer scopes all <c>SearchAsync</c> calls below this DN.
    /// </summary>
    public string BaseDn { get; init; } = string.Empty;
}
