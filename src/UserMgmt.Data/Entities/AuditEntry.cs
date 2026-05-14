namespace UserMgmt.Data.Entities;

/// <summary>
/// One row per field-level change to a tracked entity, plus direct writes from
/// service actions that do not mutate tracked entities (password resets, group
/// membership changes). Append-only at the database level — DENY UPDATE / DENY
/// DELETE is granted in the initial migration.
/// </summary>
/// <remarks>
/// Schema matches the README class diagram, plus a nullable <see cref="Reason"/>
/// column populated on Disable / Delete actions. The allowed values are
/// constrained at the DB level by a <c>CHECK</c> constraint over
/// <see cref="AuditReason.AllowedValues"/>.
/// </remarks>
public sealed class AuditEntry
{
    /// <summary>Identity column; assigned by SQL on insert.</summary>
    public long Id { get; set; }

    /// <summary>UTC timestamp of the audited change.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>UPN of the actor that performed the change (from <c>ICurrentActor</c>).</summary>
    public string ActorUpn { get; set; } = string.Empty;

    /// <summary>Action name (e.g. <c>Update</c>, <c>ResetPassword</c>, <c>Disable</c>).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>UPN of the user the action targets.</summary>
    public string TargetUpn { get; set; } = string.Empty;

    /// <summary>Name of the changed field, or empty for action-level entries.</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>String representation of the prior value (null if the column was null).</summary>
    public string? OldValue { get; set; }

    /// <summary>String representation of the new value (null if the column is now null).</summary>
    public string? NewValue { get; set; }

    /// <summary>Source surface (<c>Web</c>, <c>Api</c>, <c>MLRetrain</c>, <c>System</c>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Optional reason code, populated on Disable / Delete actions. Constrained
    /// at the database level to one of <see cref="AuditReason.AllowedValues"/>.
    /// </summary>
    public string? Reason { get; set; }
}
