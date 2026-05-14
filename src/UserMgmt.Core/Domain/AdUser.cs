namespace UserMgmt.Core.Domain;

/// <summary>
/// A user account as observed from Active Directory.
/// <see cref="Enabled"/> is computed by the service from
/// <c>userAccountControl &amp; ACCOUNTDISABLE == 0</c> — callers see a clean bool,
/// never the bitfield.
/// </summary>
public sealed record AdUser(
    string Upn,
    string SamAccountName,
    string Dn,
    string DisplayName,
    string? GivenName,
    string? Surname,
    string? Department,
    string? ManagerDn,
    string OuPath,
    DateTime WhenCreated,
    DateTime? LastLogon,
    bool Enabled);
