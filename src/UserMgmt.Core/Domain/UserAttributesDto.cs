namespace UserMgmt.Core.Domain;

/// <summary>
/// Writable sidecar fields for a user. UPN is the route key (not in the body),
/// <c>ExcludeFromMLScoring</c> is mutated through its own method
/// (<c>SetExcludeFromMlAsync</c>) so the audit row carries the right action,
/// and the concurrency token is supplied separately by the caller — none of
/// those belong in this DTO.
/// </summary>
/// <param name="CostCenter">Cost-centre code for accounting attribution.</param>
/// <param name="ContractType">Contract type (e.g. <c>Permanent</c>, <c>Contractor</c>, <c>Intern</c>).</param>
/// <param name="EmployeeId">External employee identifier from the HR system.</param>
public sealed record UserAttributesDto(
    string? CostCenter,
    string? ContractType,
    string? EmployeeId);
