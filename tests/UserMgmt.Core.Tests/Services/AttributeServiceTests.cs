using Microsoft.EntityFrameworkCore;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Tests.Fixtures;
using UserMgmt.Data.Entities;
using UserMgmt.Data.Services;

namespace UserMgmt.Core.Tests.Services;

public sealed class AttributeServiceTests
{
    private static readonly Actor TestActor = new("admin@example.org", ActorSource.Web);

    [Fact]
    public async Task GetAsync_UnknownUpn_ReturnsNull()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        var result = await sut.GetAsync("ghost@example.org");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpsertAsync_NewUpn_InsertsAndReturnsEntity()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        var dto = new UserAttributesDto(CostCenter: "CC-100", ContractType: "Permanent", EmployeeId: "E-1");

        var result = await sut.UpsertAsync("bob@example.org", dto, ifMatchRowVersion: null);

        result.IsSuccess.ShouldBeTrue();
        var inserted = result.Value!;
        inserted.Upn.ShouldBe("bob@example.org");
        inserted.CostCenter.ShouldBe("CC-100");
        inserted.ContractType.ShouldBe("Permanent");
        inserted.EmployeeId.ShouldBe("E-1");
        inserted.RowVersion.ShouldNotBe(Guid.Empty);

        // Confirm persistence through a fresh context.
        using var readContext = fixture.NewContext();
        var persisted = await readContext.UserAttributes.AsNoTracking().SingleAsync();
        persisted.CostCenter.ShouldBe("CC-100");
        persisted.RowVersion.ShouldBe(inserted.RowVersion);
    }

    [Fact]
    public async Task UpsertAsync_ExistingUpn_WithCurrentRowVersion_UpdatesAndReturnsEntity()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        var insert = await sut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-100", "Permanent", "E-1"),
            ifMatchRowVersion: null);
        insert.IsSuccess.ShouldBeTrue();
        var originalRowVersion = insert.Value!.RowVersion;

        // Read through a fresh context to mimic a real caller round-trip.
        using var freshContext = fixture.NewContext();
        var freshSut = new AttributeService(freshContext);
        var update = await freshSut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-200", "Contractor", "E-1"),
            ifMatchRowVersion: originalRowVersion);

        update.IsSuccess.ShouldBeTrue();
        var updated = update.Value!;
        updated.CostCenter.ShouldBe("CC-200");
        updated.ContractType.ShouldBe("Contractor");
        updated.RowVersion.ShouldNotBe(originalRowVersion);
    }

    [Fact]
    public async Task UpsertAsync_ExistingUpn_WithStaleRowVersion_ReturnsConcurrencyConflict()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        var insert = await sut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-100", "Permanent", "E-1"),
            ifMatchRowVersion: null);
        insert.IsSuccess.ShouldBeTrue();
        var staleRowVersion = insert.Value!.RowVersion;

        // Another writer commits an update in the meantime, bumping the token.
        using var otherContext = fixture.NewContext();
        var otherSut = new AttributeService(otherContext);
        var firstUpdate = await otherSut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-OTHER", "Contractor", "E-1"),
            ifMatchRowVersion: staleRowVersion);
        firstUpdate.IsSuccess.ShouldBeTrue();
        var newRowVersion = firstUpdate.Value!.RowVersion;
        newRowVersion.ShouldNotBe(staleRowVersion);

        // Our caller still holds the stale token and tries to commit.
        using var staleContext = fixture.NewContext();
        var staleSut = new AttributeService(staleContext);
        var conflictingUpdate = await staleSut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-MINE", "Intern", "E-1"),
            ifMatchRowVersion: staleRowVersion);

        conflictingUpdate.IsSuccess.ShouldBeFalse();
        conflictingUpdate.Error!.Attribute.ShouldBe(nameof(UserAttributes.RowVersion));
        conflictingUpdate.Error.CurrentValue.ShouldBe(newRowVersion.ToString());
    }

    [Fact]
    public async Task SetExcludeFromMlAsync_FlipsFlag_AuditRowWritten()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        var insert = await sut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-100", "Permanent", "E-1"),
            ifMatchRowVersion: null);
        insert.IsSuccess.ShouldBeTrue();
        var currentRowVersion = insert.Value!.RowVersion;
        insert.Value.ExcludeFromMLScoring.ShouldBeFalse();

        // Flip the flag. Use a fresh context so the audit assertion isn't
        // confused by carried-over change-tracker state.
        using var flipContext = fixture.NewContext();
        var flipSut = new AttributeService(flipContext);
        var result = await flipSut.SetExcludeFromMlAsync(
            "bob@example.org",
            excluded: true,
            ifMatchRowVersion: currentRowVersion);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.ExcludeFromMLScoring.ShouldBeTrue();

        // Verify the interceptor wrote an audit row for the flag change.
        using var readContext = fixture.NewContext();
        var auditRow = readContext.AuditEntries.AsNoTracking()
            .Single(e => e.Action == "Update"
                         && e.FieldName == nameof(UserAttributes.ExcludeFromMLScoring));

        auditRow.OldValue.ShouldBe("False");
        auditRow.NewValue.ShouldBe("True");
        auditRow.ActorUpn.ShouldBe(TestActor.Upn);
        auditRow.Source.ShouldBe(TestActor.Source.ToString());
        auditRow.TargetUpn.ShouldBe("bob@example.org");
    }

    [Fact]
    public async Task SetExcludeFromMlAsync_StaleRowVersion_ReturnsConcurrencyConflict()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        var insert = await sut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-100", "Permanent", "E-1"),
            ifMatchRowVersion: null);
        insert.IsSuccess.ShouldBeTrue();
        var staleRowVersion = insert.Value!.RowVersion;

        // Concurrent writer commits an unrelated upsert, bumping the token.
        using var otherContext = fixture.NewContext();
        var otherSut = new AttributeService(otherContext);
        var bumpResult = await otherSut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-200", "Contractor", "E-1"),
            ifMatchRowVersion: staleRowVersion);
        bumpResult.IsSuccess.ShouldBeTrue();
        var currentRowVersion = bumpResult.Value!.RowVersion;

        // Our caller still has the stale token and tries the ML flip.
        using var flipContext = fixture.NewContext();
        var flipSut = new AttributeService(flipContext);
        var result = await flipSut.SetExcludeFromMlAsync(
            "bob@example.org",
            excluded: true,
            ifMatchRowVersion: staleRowVersion);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Attribute.ShouldBe(nameof(UserAttributes.RowVersion));
        result.Error.CurrentValue.ShouldBe(currentRowVersion.ToString());
    }

    [Fact]
    public async Task UpsertAsync_ConcurrencyConflict_NoAuditRowsWritten()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AttributeService(fixture.Context);

        // Seed a row.
        var insert = await sut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-100", "Permanent", "E-1"),
            ifMatchRowVersion: null);
        insert.IsSuccess.ShouldBeTrue();
        var staleRowVersion = insert.Value!.RowVersion;

        // Concurrent writer bumps the token.
        using var otherContext = fixture.NewContext();
        var otherSut = new AttributeService(otherContext);
        var firstUpdate = await otherSut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-OTHER", "Contractor", "E-1"),
            ifMatchRowVersion: staleRowVersion);
        firstUpdate.IsSuccess.ShouldBeTrue();

        // Snapshot audit-row count after the (successful) preceding writes.
        using var snapshotContext = fixture.NewContext();
        int auditCountBefore = await snapshotContext.AuditEntries.AsNoTracking().CountAsync();

        // Our caller tries to commit with the stale token — must fail.
        using var staleContext = fixture.NewContext();
        var staleSut = new AttributeService(staleContext);
        var conflict = await staleSut.UpsertAsync(
            "bob@example.org",
            new UserAttributesDto("CC-MINE", "Intern", "E-1"),
            ifMatchRowVersion: staleRowVersion);

        conflict.IsSuccess.ShouldBeFalse();

        // Re-count: the failed upsert must NOT have written any audit rows.
        using var verifyContext = fixture.NewContext();
        int auditCountAfter = await verifyContext.AuditEntries.AsNoTracking().CountAsync();
        auditCountAfter.ShouldBe(auditCountBefore);
    }
}
