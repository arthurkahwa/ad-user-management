namespace UserMgmt.Core.Auth;

/// <summary>
/// Marks an entity property whose value must never appear in audit rows.
/// Used for password fields, cached score values, and other sensitive or noisy data.
/// </summary>
/// <remarks>
/// The audit <c>SaveChangesInterceptor</c> in <c>UserMgmt.Data</c> inspects each
/// modified property and skips any that carry this attribute. There is no
/// runtime opt-in — once applied, the property is unconditionally excluded.
/// </remarks>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class AuditIgnoreAttribute : Attribute
{
}
