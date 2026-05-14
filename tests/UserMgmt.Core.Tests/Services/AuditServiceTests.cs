using Microsoft.EntityFrameworkCore;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Services;
using UserMgmt.Core.Tests.Fixtures;
using UserMgmt.Data.Entities;
using UserMgmt.Data.Services;

namespace UserMgmt.Core.Tests.Services;

public sealed class AuditServiceTests
{
    private static readonly Actor TestActor = new("admin@example.org", ActorSource.Web);

    [Fact]
    public async Task RecordAsync_PersistsRowWithActorUpnAndSourceFromCurrentActor()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        var dto = new AuditEntryDto(
            Id: 0,
            Timestamp: new DateTime(2026, 5, 13, 10, 0, 0, DateTimeKind.Utc),
            ActorUpn: "ignored@example.org", // service must overwrite with ICurrentActor
            Action: "ResetPassword",
            TargetUpn: "bob@example.org",
            FieldName: string.Empty,
            OldValue: null,
            NewValue: null,
            Source: "ignored",
            Reason: null);

        await sut.RecordAsync(dto);

        var persisted = await fixture.Context.AuditEntries.AsNoTracking().SingleAsync();
        persisted.ActorUpn.ShouldBe(TestActor.Upn);
        persisted.Source.ShouldBe(TestActor.Source.ToString());
        persisted.Action.ShouldBe("ResetPassword");
        persisted.TargetUpn.ShouldBe("bob@example.org");
        persisted.Timestamp.ShouldBe(new DateTime(2026, 5, 13, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task RecordAsync_DefaultTimestamp_UsesInjectedTimeProvider()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new StubTimeProvider(fixedTime);

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor), timeProvider);
        var sut = new AuditService(fixture.Context, fixture.CurrentActor, timeProvider);

        var dto = new AuditEntryDto(
            Id: 0,
            Timestamp: default,
            ActorUpn: string.Empty,
            Action: "Disable",
            TargetUpn: "bob@example.org",
            FieldName: string.Empty,
            OldValue: null,
            NewValue: null,
            Source: string.Empty,
            Reason: AuditReason.Stale);

        await sut.RecordAsync(dto);

        var persisted = await fixture.Context.AuditEntries.AsNoTracking().SingleAsync();
        persisted.Timestamp.ShouldBe(fixedTime.UtcDateTime);
        persisted.Reason.ShouldBe(AuditReason.Stale);
    }

    [Fact]
    public async Task RecordAsync_ReasonOutsideAllowedSet_RejectedByCheckConstraint()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        var dto = new AuditEntryDto(
            Id: 0,
            Timestamp: DateTime.UtcNow,
            ActorUpn: string.Empty,
            Action: "Disable",
            TargetUpn: "bob@example.org",
            FieldName: string.Empty,
            OldValue: null,
            NewValue: null,
            Source: string.Empty,
            Reason: "MysteryReason"); // not in the allowed set

        // SQLite raises a CHECK-constraint error here; EF Core surfaces it as
        // DbUpdateException with an inner SqliteException.
        await Should.ThrowAsync<DbUpdateException>(async () => await sut.RecordAsync(dto));
    }

    [Theory]
    [InlineData(AuditReason.Stale)]
    [InlineData(AuditReason.Termination)]
    [InlineData(AuditReason.Reorg)]
    [InlineData(AuditReason.Compromise)]
    public async Task RecordAsync_AllowedReason_RoundTripsCorrectly(string reason)
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        await sut.RecordAsync(new AuditEntryDto(
            Id: 0,
            Timestamp: DateTime.UtcNow,
            ActorUpn: string.Empty,
            Action: "Disable",
            TargetUpn: "bob@example.org",
            FieldName: string.Empty,
            OldValue: null,
            NewValue: null,
            Source: string.Empty,
            Reason: reason));

        var persisted = await fixture.Context.AuditEntries.AsNoTracking().SingleAsync();
        persisted.Reason.ShouldBe(reason);
    }

    [Fact]
    public async Task QueryForUserAsync_FiltersByTargetUpnAndPagesNewestFirst()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        // 7 audit rows for bob, plus 2 for alice (should not appear in result).
        for (int i = 0; i < 7; i++)
        {
            await sut.RecordAsync(new AuditEntryDto(
                Id: 0,
                Timestamp: new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                ActorUpn: string.Empty,
                Action: "Update",
                TargetUpn: "bob@example.org",
                FieldName: $"field{i}",
                OldValue: null,
                NewValue: $"v{i}",
                Source: string.Empty,
                Reason: null));
        }

        for (int i = 0; i < 2; i++)
        {
            await sut.RecordAsync(new AuditEntryDto(
                Id: 0,
                Timestamp: new DateTime(2026, 5, 1, 11, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                ActorUpn: string.Empty,
                Action: "Update",
                TargetUpn: "alice@example.org",
                FieldName: $"alice-field{i}",
                OldValue: null,
                NewValue: $"av{i}",
                Source: string.Empty,
                Reason: null));
        }

        var page1 = await sut.QueryForUserAsync("bob@example.org", page: 1, pageSize: 3);

        page1.TotalCount.ShouldBe(7);
        page1.Page.ShouldBe(1);
        page1.PageSize.ShouldBe(3);
        page1.Items.Count.ShouldBe(3);
        // Newest first: minute 6, 5, 4.
        page1.Items.Select(i => i.FieldName).ShouldBe(["field6", "field5", "field4"]);

        var page2 = await sut.QueryForUserAsync("bob@example.org", page: 2, pageSize: 3);
        page2.Items.Select(i => i.FieldName).ShouldBe(["field3", "field2", "field1"]);

        var page3 = await sut.QueryForUserAsync("bob@example.org", page: 3, pageSize: 3);
        page3.Items.Count.ShouldBe(1);
        page3.Items[0].FieldName.ShouldBe("field0");

        // None of bob's pages contain alice's rows.
        page1.Items.ShouldAllBe(i => i.TargetUpn == "bob@example.org");
        page2.Items.ShouldAllBe(i => i.TargetUpn == "bob@example.org");
        page3.Items.ShouldAllBe(i => i.TargetUpn == "bob@example.org");
    }

    [Fact]
    public async Task QueryForUserAsync_NoMatches_ReturnsEmptyPage()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        var page = await sut.QueryForUserAsync("ghost@example.org", page: 1, pageSize: 10);

        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task QueryForUserAsync_BlankUpn_Throws(string? upn)
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await sut.QueryForUserAsync(upn!, 1, 10));
    }

    [Fact]
    public async Task QueryForUserAsync_NonPositivePage_Throws()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await sut.QueryForUserAsync("bob@example.org", page: 0, pageSize: 10));
    }

    [Fact]
    public async Task QueryForUserAsync_NonPositivePageSize_Throws()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await sut.QueryForUserAsync("bob@example.org", page: 1, pageSize: 0));
    }

    [Fact]
    public async Task Interceptor_EmitsOneAuditEntryPerModifiedField_WithActorUpnFromCurrentActor()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));

        var attrs = new UserAttributes
        {
            Upn = "bob@example.org",
            CostCenter = "CC-100",
            ContractType = "Permanent",
        };
        fixture.Context.UserAttributes.Add(attrs);
        await fixture.Context.SaveChangesAsync();

        // The Add itself emits create-rows. Clear those out for a clean assertion
        // window over the update.
        var attached = fixture.Context.AuditEntries.ToList();
        fixture.Context.AuditEntries.RemoveRange(attached);
        await fixture.Context.SaveChangesAsync();

        // Now mutate two fields.
        attrs.CostCenter = "CC-200";
        attrs.ContractType = "Contractor";
        await fixture.Context.SaveChangesAsync();

        // Read through a second context to confirm rows are committed and visible.
        using var readContext = fixture.NewContext();
        var rows = readContext.AuditEntries.AsNoTracking()
            .Where(e => e.Action == "Update")
            .ToList();

        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.ActorUpn == TestActor.Upn);
        rows.ShouldAllBe(r => r.Source == TestActor.Source.ToString());
        rows.ShouldAllBe(r => r.TargetUpn == "bob@example.org");

        var costCenterRow = rows.Single(r => r.FieldName == nameof(UserAttributes.CostCenter));
        costCenterRow.OldValue.ShouldBe("CC-100");
        costCenterRow.NewValue.ShouldBe("CC-200");

        var contractRow = rows.Single(r => r.FieldName == nameof(UserAttributes.ContractType));
        contractRow.OldValue.ShouldBe("Permanent");
        contractRow.NewValue.ShouldBe("Contractor");
    }

    [Fact]
    public async Task Interceptor_AuditIgnoreAttribute_PropertyNeverAppearsInAudit()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));

        var attrs = new UserAttributes
        {
            Upn = "bob@example.org",
            CostCenter = "CC-100",
            StaleRiskScore = 0.1f, // [AuditIgnore] — must not appear in audit
        };
        fixture.Context.UserAttributes.Add(attrs);
        await fixture.Context.SaveChangesAsync();

        // Update the ignored field.
        attrs.StaleRiskScore = 0.9f;
        await fixture.Context.SaveChangesAsync();

        var allFields = fixture.Context.AuditEntries.AsNoTracking()
            .Select(e => e.FieldName)
            .ToList();

        allFields.ShouldNotContain(nameof(UserAttributes.StaleRiskScore));
    }

    [Fact]
    public async Task Interceptor_ActorChangesBetweenContexts_AuditRowReflectsActiveActor()
    {
        var aliceActor = new Actor("alice@example.org", ActorSource.Web);
        var bobActor = new Actor("bob@example.org", ActorSource.Api);

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(aliceActor));

        fixture.Context.UserAttributes.Add(new UserAttributes
        {
            Upn = "charlie@example.org",
            CostCenter = "X",
        });
        await fixture.Context.SaveChangesAsync();

        // Switch context to one with a different actor and modify.
        using var bobContext = fixture.NewContext(new StubCurrentActor(bobActor));
        var attrs = bobContext.UserAttributes.Single();
        attrs.CostCenter = "Y";
        await bobContext.SaveChangesAsync();

        var updateRow = fixture.Context.AuditEntries.AsNoTracking()
            .Single(e => e.Action == "Update" && e.FieldName == nameof(UserAttributes.CostCenter));

        updateRow.ActorUpn.ShouldBe(bobActor.Upn);
        updateRow.Source.ShouldBe(bobActor.Source.ToString());
    }

    [Fact]
    public async Task Interceptor_UnchangedField_DoesNotEmitRow()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));

        fixture.Context.UserAttributes.Add(new UserAttributes
        {
            Upn = "bob@example.org",
            CostCenter = "CC-100",
            ContractType = "Permanent",
        });
        await fixture.Context.SaveChangesAsync();

        // Clear out the create rows.
        fixture.Context.AuditEntries.RemoveRange(fixture.Context.AuditEntries.ToList());
        await fixture.Context.SaveChangesAsync();

        // Touch the entity but don't actually change anything.
        var attrs = fixture.Context.UserAttributes.Single();
        attrs.CostCenter = "CC-100"; // same value
        await fixture.Context.SaveChangesAsync();

        var updateRows = fixture.Context.AuditEntries.AsNoTracking()
            .Where(e => e.Action == "Update")
            .ToList();

        updateRows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Interceptor_DoesNotRecursivelyAuditAuditEntryWrites()
    {
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var sut = new AuditService(fixture.Context, fixture.CurrentActor);

        await sut.RecordAsync(new AuditEntryDto(
            Id: 0,
            Timestamp: DateTime.UtcNow,
            ActorUpn: string.Empty,
            Action: "ResetPassword",
            TargetUpn: "bob@example.org",
            FieldName: string.Empty,
            OldValue: null,
            NewValue: null,
            Source: string.Empty,
            Reason: null));

        int totalCount = await fixture.Context.AuditEntries.AsNoTracking().CountAsync();
        // Exactly the row we recorded — no recursive shadow rows.
        totalCount.ShouldBe(1);
    }

    /// <summary>Test-only TimeProvider returning a fixed instant.</summary>
    private sealed class StubTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public StubTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
