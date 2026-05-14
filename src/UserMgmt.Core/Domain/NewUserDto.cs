namespace UserMgmt.Core.Domain;

/// <summary>
/// Input to <c>IAdService.CreateAsync</c>. Carries both the AD-bound fields and
/// the sidecar attributes that <c>AttributeService</c> persists in SQL.
/// </summary>
/// <remarks>
/// Sidecar attributes (<see cref="CostCenter"/>, <see cref="ContractType"/>,
/// <see cref="EmployeeId"/>) are deliberately not mirrored into AD —
/// see README §Cross-store consistency.
/// </remarks>
public sealed record NewUserDto(
    string Upn,
    string SamAccountName,
    string GivenName,
    string Surname,
    string DisplayName,
    string OuPath,
    string? Department,
    string? ManagerDn,
    string? CostCenter,
    string? ContractType,
    string? EmployeeId);
