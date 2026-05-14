using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UserMgmt.Core.Auth;
using UserMgmt.Data;
using UserMgmt.Data.Interceptors;

namespace UserMgmt.Core.Tests.Fixtures;

/// <summary>
/// Per-test SQLite in-memory <see cref="UserMgmtDbContext"/> harness.
/// </summary>
/// <remarks>
/// Each instance opens a fresh in-memory connection, builds a context against
/// it, and runs <c>EnsureCreated</c> so the schema is materialised. The
/// connection stays open for the lifetime of the fixture; when disposed both
/// the connection and the context are released.
/// <para>
/// The EF Core in-memory provider is not used because it does not enforce
/// <c>CHECK</c> constraints or <c>RowVersion</c> semantics — both load-bearing
/// for these tests.
/// </para>
/// </remarks>
public sealed class SqliteDbContextFixture : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Create a fixture with a real <see cref="AuditSaveChangesInterceptor"/>.</summary>
    public SqliteDbContextFixture(ICurrentActor? actor = null, TimeProvider? timeProvider = null)
    {
        CurrentActor = actor ?? new StubCurrentActor(new Actor("system@local", ActorSource.System));
        TimeProvider = timeProvider ?? System.TimeProvider.System;

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Interceptor = new AuditSaveChangesInterceptor(CurrentActor, TimeProvider);

        var options = new DbContextOptionsBuilder<UserMgmtDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(Interceptor)
            .Options;

        Context = new UserMgmtDbContext(options);
        Context.Database.EnsureCreated();
    }

    /// <summary>The actor surfaced to the audit interceptor.</summary>
    public ICurrentActor CurrentActor { get; }

    /// <summary>The interceptor under test.</summary>
    public AuditSaveChangesInterceptor Interceptor { get; }

    /// <summary>The clock the interceptor reads timestamps from.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>The context. Owned by the fixture.</summary>
    public UserMgmtDbContext Context { get; }

    /// <summary>
    /// Create a *second* <see cref="UserMgmtDbContext"/> backed by the same
    /// SQLite connection. Useful for verifying that audit rows persist across
    /// contexts and to simulate concurrent edits.
    /// </summary>
    public UserMgmtDbContext NewContext(ICurrentActor? overrideActor = null)
    {
        var interceptor = overrideActor is null
            ? Interceptor
            : new AuditSaveChangesInterceptor(overrideActor, TimeProvider);

        var options = new DbContextOptionsBuilder<UserMgmtDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;

        return new UserMgmtDbContext(options);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
