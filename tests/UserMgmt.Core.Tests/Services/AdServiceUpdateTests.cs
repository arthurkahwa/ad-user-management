using System.DirectoryServices.Protocols;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Common;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Ldap;
using UserMgmt.Core.Services;
using UserMgmt.Core.Tests.Fixtures;
using UserMgmt.Data.Entities;
using UserMgmt.Data.Services;

namespace UserMgmt.Core.Tests.Services;

public sealed class AdServiceUpdateTests
{
    private const string BaseDn = "DC=example,DC=corp";
    private const string TargetUpn = "alice@example.corp";
    private const string TargetDn = "CN=Alice,OU=People,DC=example,DC=corp";

    private static readonly Actor TestActor = new("admin@example.corp", ActorSource.Web);

    /// <summary>
    /// Build a search response with a single entry matching <see cref="TargetUpn"/>
    /// — used by <see cref="AdService.UpdateAsync"/>'s up-front
    /// <see cref="AdService.GetAsync"/> call to resolve the target DN.
    /// </summary>
    private static SearchResponse BuildTargetUserSearch()
    {
        SearchResultEntry entry = SearchResultEntryBuilder.Build(
            TargetDn,
            new Dictionary<string, object?>
            {
                ["sAMAccountName"] = "alice",
                ["userPrincipalName"] = TargetUpn,
                ["displayName"] = "Alice Smith",
                ["whenCreated"] = "20260101120000.0Z",
                ["userAccountControl"] = "512",
            });
        return SearchResultEntryBuilder.BuildResponse([entry]);
    }

    private static AdService BuildService(
        IAdConnection connection,
        IAttributeService attributeService,
        IAuditService auditService)
    {
        IOptions<AdOptions> options = Options.Create(new AdOptions
        {
            BaseDn = BaseDn,
            Port = 636,
        });
        return new AdService(connection, options, NullLogger<AdService>.Instance, attributeService, auditService);
    }

    [Fact]
    public async Task UpdateAsync_SingleAdAttribute_EmitsDeleteOldAddNewInOneModifyRequest()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(BuildTargetUserSearch()));
        connection.ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(ModifyResponseBuilder.BuildSuccess()));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?> { ["department"] = "Sales" };
        var ifMatch = new Dictionary<string, string?> { ["department"] = "Engineering" };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeTrue();
        await connection.Received(1).ModifyAsync(
            Arg.Is<ModifyRequest>(r =>
                r.DistinguishedName == TargetDn
                && r.Modifications.Count == 2
                && r.Modifications[0].Operation == DirectoryAttributeOperation.Delete
                && r.Modifications[0].Name == "department"
                && (string)r.Modifications[0][0] == "Engineering"
                && r.Modifications[1].Operation == DirectoryAttributeOperation.Add
                && r.Modifications[1].Name == "department"
                && (string)r.Modifications[1][0] == "Sales"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_StaleIfMatchValue_ReturnsConcurrencyConflict_NamingTheAttribute()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(BuildTargetUserSearch()));
        // First ModifyAsync (the CAS attempt) throws NoSuchAttribute → CAS miss.
        connection.ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ModifyResponse>>(_ => throw ModifyResponseBuilder.BuildOperationException(ResultCode.NoSuchAttribute));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?> { ["department"] = "Sales" };
        var ifMatch = new Dictionary<string, string?> { ["department"] = "STALE-VALUE" };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeFalse();
        var err = result.Error.ShouldBeOfType<UpdateUserError.Concurrency>();
        err.Conflict.Attribute.ShouldBe("department");
    }

    [Fact]
    public async Task UpdateAsync_MultipleAttributes_OneModifyRequestPerAttribute()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(BuildTargetUserSearch()));
        connection.ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(ModifyResponseBuilder.BuildSuccess()));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?>
        {
            ["displayName"] = "Alice Smith-Jones",
            ["department"] = "Sales",
            ["telephoneNumber"] = "+44-20-1234",
        };
        var ifMatch = new Dictionary<string, string?>
        {
            ["displayName"] = "Alice Smith",
            ["department"] = "Engineering",
            ["telephoneNumber"] = "+44-20-0000",
        };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeTrue();
        // One ModifyRequest per attribute — three total.
        await connection.Received(3).ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>());
        // Every emitted request carries exactly two modification ops (delete-old + add-new).
        await connection.Received(3).ModifyAsync(
            Arg.Is<ModifyRequest>(r => r.Modifications.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_FirstAdConflict_StopsProcessing_LaterAttributesUntouched()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(BuildTargetUserSearch()));

        // First Modify (department) raises a CAS miss; we should never see a
        // Modify call for displayName afterwards.
        connection.ModifyAsync(
                Arg.Is<ModifyRequest>(r => r.Modifications.Count > 0 && r.Modifications[0].Name == "department"),
                Arg.Any<CancellationToken>())
            .Returns<Task<ModifyResponse>>(_ => throw ModifyResponseBuilder.BuildOperationException(ResultCode.NoSuchAttribute));
        connection.ModifyAsync(
                Arg.Is<ModifyRequest>(r => r.Modifications.Count > 0 && r.Modifications[0].Name != "department"),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(ModifyResponseBuilder.BuildSuccess()));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        // Use a sorted dictionary so iteration order is deterministic and
        // "department" comes before "displayName".
        var changes = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["department"] = "Sales",
            ["displayName"] = "Alice Smith-Jones",
        };
        var ifMatch = new SortedDictionary<string, string?>(StringComparer.Ordinal)
        {
            ["department"] = "STALE",
            ["displayName"] = "Alice Smith",
        };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeFalse();
        var err = result.Error.ShouldBeOfType<UpdateUserError.Concurrency>();
        err.Conflict.Attribute.ShouldBe("department");

        // The displayName modify must NOT have been attempted.
        await connection.DidNotReceive().ModifyAsync(
            Arg.Is<ModifyRequest>(r => r.Modifications.Count > 0 && r.Modifications[0].Name == "displayName"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_SidecarAttribute_RoutesToAttributeService()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        // No AD changes → no SearchAsync, no ModifyAsync expected.

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?>
        {
            ["CostCenter"] = "CC-200",
            ["ContractType"] = "Permanent",
            ["EmployeeId"] = "E-42",
        };
        var ifMatch = new Dictionary<string, string?>();

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeTrue();

        // No AD round-trip.
        await connection.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().ModifyAsync(default!, default);

        // The sidecar row was inserted.
        using var read = fixture.NewContext();
        var row = await read.UserAttributes.AsNoTracking().SingleAsync();
        row.Upn.ShouldBe(TargetUpn);
        row.CostCenter.ShouldBe("CC-200");
        row.ContractType.ShouldBe("Permanent");
        row.EmployeeId.ShouldBe("E-42");
    }

    [Fact]
    public async Task UpdateAsync_MixedAdAndSidecar_AdReceivesModifyRequests_SidecarReceivesUpsert()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(BuildTargetUserSearch()));
        connection.ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(ModifyResponseBuilder.BuildSuccess()));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?>
        {
            ["department"] = "Sales",
            ["CostCenter"] = "CC-200",
        };
        var ifMatch = new Dictionary<string, string?>
        {
            ["department"] = "Engineering",
        };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeTrue();

        // AD got the ModifyRequest.
        await connection.Received(1).ModifyAsync(
            Arg.Is<ModifyRequest>(r => r.Modifications[0].Name == "department"),
            Arg.Any<CancellationToken>());

        // Sidecar row was inserted via AttributeService.
        using var read = fixture.NewContext();
        var row = await read.UserAttributes.AsNoTracking().SingleAsync();
        row.CostCenter.ShouldBe("CC-200");
    }

    [Fact]
    public async Task UpdateAsync_StaleSidecarToken_ReturnsConcurrencyConflict_FromAttributeService()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        // No AD attributes → AD layer not touched.

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        // Seed an existing sidecar row so the stale-token path is exercised.
        var seed = await attrSvc.UpsertAsync(
            TargetUpn,
            new UserAttributesDto("CC-OLD", "Permanent", "E-1"),
            ifMatchRowVersion: null);
        seed.IsSuccess.ShouldBeTrue();
        var currentRowVersion = seed.Value!.RowVersion;

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?>
        {
            ["CostCenter"] = "CC-200",
            ["ContractType"] = "Contractor",
            ["EmployeeId"] = "E-1",
        };
        var ifMatch = new Dictionary<string, string?>();

        // Use a fresh, deliberately wrong token.
        Guid staleToken = Guid.NewGuid();
        staleToken.ShouldNotBe(currentRowVersion);

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: staleToken);

        result.IsSuccess.ShouldBeFalse();
        var err = result.Error.ShouldBeOfType<UpdateUserError.Concurrency>();
        err.Conflict.Attribute.ShouldBe(nameof(UserAttributes.RowVersion));
    }

    [Fact]
    public async Task UpdateAsync_UnknownAttribute_ReturnsUnknownAttribute_NoSideEffects()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?>
        {
            ["thisIsNotARealAttribute"] = "x",
        };
        var ifMatch = new Dictionary<string, string?>
        {
            ["thisIsNotARealAttribute"] = "y",
        };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);

        result.IsSuccess.ShouldBeFalse();
        var err = result.Error.ShouldBeOfType<UpdateUserError.UnknownAttribute>();
        err.Name.ShouldBe("thisIsNotARealAttribute");

        // No AD or sidecar side effects.
        await connection.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().ModifyAsync(default!, default);
        (await fixture.Context.UserAttributes.AsNoTracking().AnyAsync()).ShouldBeFalse();
        // No audit row either.
        (await fixture.Context.AuditEntries.AsNoTracking().AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public void UpdateAsync_DoesNotReadWhenChanged_VerifiedByFileGrep()
    {
        // Source file ships alongside the assembly under
        // src/UserMgmt.Core/Services/AdService.Update.cs. Walk up from the
        // test assembly path to locate it; this is robust against changes
        // in test-runner working directory.
        string sourcePath = LocateSourceFile("src/UserMgmt.Core/Services/AdService.Update.cs");
        string contents = File.ReadAllText(sourcePath);

        // The literal must not appear anywhere in the file — neither in
        // code nor in comments. The grep is intentionally strict.
        contents.ShouldNotContain("whenChanged", Case.Sensitive);
    }

    [Fact]
    public async Task UpdateAsync_AdChangeEmitsAuditRow_SidecarChangeEmittedByInterceptor_NoDoubleAudit()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(BuildTargetUserSearch()));
        connection.ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(ModifyResponseBuilder.BuildSuccess()));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        AttributeService attrSvc = new(fixture.Context);
        AuditService auditSvc = new(fixture.Context, fixture.CurrentActor);

        AdService sut = BuildService(connection, attrSvc, auditSvc);

        var changes = new Dictionary<string, string?>
        {
            ["department"] = "Sales",
            ["CostCenter"] = "CC-200",
        };
        var ifMatch = new Dictionary<string, string?>
        {
            ["department"] = "Engineering",
        };

        var result = await sut.UpdateAsync(TargetUpn, changes, ifMatch, ifMatchSidecarToken: null);
        result.IsSuccess.ShouldBeTrue();

        using var read = fixture.NewContext();
        var auditRows = await read.AuditEntries.AsNoTracking()
            .Where(e => e.TargetUpn == TargetUpn)
            .ToListAsync();

        // Exactly one row for the AD attribute change (emitted by
        // AuditService.RecordAsync), and one row per non-ignored sidecar
        // field change captured by the interceptor.
        //
        // The interceptor emits one row PER scalar property on the inserted
        // UserAttributes entity. Insert/Add state means it emits Create
        // rows for non-ignored properties (Upn, EmployeeId, CostCenter,
        // ContractType, ExcludeFromMLScoring). The "department" AD change
        // adds exactly one Update row.
        auditRows.Count(e => e.FieldName == "department").ShouldBe(1);
        auditRows.Where(e => e.FieldName == "department").Single().Action.ShouldBe("Update");
        auditRows.Where(e => e.FieldName == "department").Single().OldValue.ShouldBe("Engineering");
        auditRows.Where(e => e.FieldName == "department").Single().NewValue.ShouldBe("Sales");

        // Sidecar rows captured by the interceptor — at least the CostCenter
        // field is recorded.
        auditRows.Any(e => e.FieldName == nameof(UserAttributes.CostCenter)).ShouldBeTrue();

        // No double-auditing: the AD-side row is the only one carrying
        // FieldName == "department".
        auditRows.Count(e => e.FieldName == "department").ShouldBe(1);
    }

    /// <summary>
    /// Walk up from the test assembly's bin directory until we find a
    /// directory containing <c>UserMgmt.slnx</c>, then resolve the
    /// requested relative path.
    /// </summary>
    private static string LocateSourceFile(string relativePath)
    {
        string? directory = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && directory is not null; i++)
        {
            if (File.Exists(Path.Combine(directory, "UserMgmt.slnx")))
            {
                return Path.Combine(directory, relativePath);
            }
            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException(
            $"Could not locate repository root (UserMgmt.slnx) starting from {AppContext.BaseDirectory}.");
    }
}
