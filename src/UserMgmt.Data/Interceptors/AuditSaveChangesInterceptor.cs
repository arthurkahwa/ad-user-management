using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using UserMgmt.Core.Auth;
using UserMgmt.Data.Entities;

namespace UserMgmt.Data.Interceptors;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that emits one <see cref="AuditEntry"/>
/// per changed field on tracked entities before commit, stamping each row with the
/// actor UPN read from the injected <see cref="ICurrentActor"/>.
/// </summary>
/// <remarks>
/// Properties carrying <see cref="AuditIgnoreAttribute"/> are unconditionally
/// excluded from the emitted rows. The <see cref="AuditEntry"/> entity itself
/// is skipped so audit writes don't recursively produce audit rows.
/// <para>
/// The interceptor only emits rows for non-shadow scalar properties that
/// actually changed (<c>IsModified</c> is true AND the original and current
/// values differ). Adding or deleting an entity is logged as a single row
/// per tracked field with <c>Action</c> = <c>Create</c> / <c>Delete</c>.
/// </para>
/// </remarks>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// Per-CLR-type cache of property names that carry <see cref="AuditIgnoreAttribute"/>.
    /// Reflection on every <c>SaveChanges</c> would otherwise allocate the same set repeatedly.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, HashSet<string>> IgnoredPropertyCache = new();

    private readonly ICurrentActor _currentActor;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Create a new interceptor.
    /// </summary>
    /// <param name="currentActor">Source of the actor UPN to stamp on each audit row.</param>
    /// <param name="timeProvider">
    /// Source of UTC <c>Now</c>; defaults to <see cref="TimeProvider.System"/>
    /// when not supplied. Tests inject a fake clock here.
    /// </param>
    public AuditSaveChangesInterceptor(ICurrentActor currentActor, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(currentActor);
        _currentActor = currentActor;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        EmitAuditRows(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        EmitAuditRows(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void EmitAuditRows(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var actor = _currentActor.Current;
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime;

        // ChangeTracker.Entries() returns the change set in a stable enumeration.
        // We materialise into a list so we can add new AuditEntry rows without
        // mutating the iterator we're walking.
        var trackedEntries = context.ChangeTracker
            .Entries()
            .Where(static e => e.Entity is not AuditEntry)
            .Where(static e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();

        foreach (var entry in trackedEntries)
        {
            var ignored = GetIgnoredProperties(entry.Entity.GetType());
            string targetUpn = ExtractTargetUpn(entry);
            string action = entry.State switch
            {
                EntityState.Added => "Create",
                EntityState.Modified => "Update",
                EntityState.Deleted => "Delete",
                _ => "Unknown",
            };

            foreach (var property in entry.Properties)
            {
                if (property.Metadata.IsShadowProperty())
                {
                    continue;
                }

                if (ignored.Contains(property.Metadata.Name))
                {
                    continue;
                }

                if (!ShouldEmit(entry.State, property))
                {
                    continue;
                }

                context.Set<AuditEntry>().Add(new AuditEntry
                {
                    Timestamp = timestamp,
                    ActorUpn = actor.Upn,
                    Action = action,
                    TargetUpn = targetUpn,
                    FieldName = property.Metadata.Name,
                    OldValue = entry.State == EntityState.Added ? null : Stringify(property.OriginalValue),
                    NewValue = entry.State == EntityState.Deleted ? null : Stringify(property.CurrentValue),
                    Source = actor.Source.ToString(),
                });
            }
        }
    }

    /// <summary>
    /// Emit a row for added / deleted entities (so the full row's shape is captured)
    /// and for modified entities only when the value has actually changed.
    /// </summary>
    private static bool ShouldEmit(EntityState state, PropertyEntry property)
    {
        if (state == EntityState.Modified)
        {
            return property.IsModified && !Equals(property.OriginalValue, property.CurrentValue);
        }

        // Added / Deleted: emit one row per scalar property.
        return true;
    }

    private static string ExtractTargetUpn(EntityEntry entry)
    {
        // UserAttributes uses Upn as the primary key. For other entities, fall back
        // to a "TargetUpn" property if present (ReconciliationQueue), or the empty
        // string. The interceptor remains correct even for entities that don't
        // surface a UPN — those rows just carry an empty TargetUpn.
        var upnProperty = entry.Metadata.FindProperty("Upn") ?? entry.Metadata.FindProperty("TargetUpn");
        if (upnProperty is null)
        {
            return string.Empty;
        }

        object? value = entry.Property(upnProperty.Name).CurrentValue ?? entry.Property(upnProperty.Name).OriginalValue;
        return value?.ToString() ?? string.Empty;
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static HashSet<string> GetIgnoredProperties(Type entityType) =>
        IgnoredPropertyCache.GetOrAdd(entityType, static t =>
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<AuditIgnoreAttribute>() is not null)
                {
                    set.Add(prop.Name);
                }
            }

            return set;
        });
}
