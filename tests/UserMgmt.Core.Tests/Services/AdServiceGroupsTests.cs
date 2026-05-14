using System.DirectoryServices.Protocols;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Common;
using UserMgmt.Core.Ldap;
using UserMgmt.Core.Services;
using UserMgmt.Core.Tests.Fixtures;
using UserMgmt.Data.Services;

namespace UserMgmt.Core.Tests.Services;

/// <summary>
/// M1.7 unit tests covering <see cref="AdService.AddToGroupAsync"/> and
/// <see cref="AdService.RemoveFromGroupAsync"/>. <see cref="IAdConnection"/>
/// is mocked with NSubstitute; the audit service is a real
/// <see cref="AuditService"/> over the SQLite-in-memory fixture so audit-row
/// assertions exercise the same code path as production.
/// </summary>
public sealed class AdServiceGroupsTests
{
    private const string BaseDn = "DC=example,DC=corp";
    private const string AliceUpn = "alice@example.corp";
    private const string AliceDn = "CN=Alice Smith,OU=Engineering," + BaseDn;
    private const string GroupDn = "CN=Engineers,OU=Groups," + BaseDn;
    private const string OtherUserDn = "CN=Bob,OU=Engineering," + BaseDn;

    private static readonly Actor TestActor = new("admin@example.org", ActorSource.Web);

    private static AdService BuildService(
        IAdConnection connection,
        IAuditService auditService,
        ICurrentActor currentActor) =>
        new(
            connection,
            Options.Create(new AdOptions { BaseDn = BaseDn, Port = 636 }),
            NullLogger<AdService>.Instance,
            auditService,
            currentActor);

    private static SearchResultEntry BuildUserEntry(string dn) =>
        SearchResultEntryBuilder.Build(dn, new Dictionary<string, object?>
        {
            ["distinguishedName"] = dn,
        });

    private static SearchResultEntry BuildGroupEntry(string dn, params string[] members) =>
        SearchResultEntryBuilder.Build(dn, members.Length == 0
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>
            {
                ["member"] = members,
            });

    /// <summary>
    /// Wire the mocked <see cref="IAdConnection.SearchAsync"/> to return the
    /// user lookup result first and the group lookup result second. Returning
    /// based on call ordinal is sufficient because every method under test
    /// issues at most two SearchAsync calls in this fixed order.
    /// </summary>
    private static void StubSearchSequence(
        IAdConnection connection,
        SearchResponse userResponse,
        SearchResponse groupResponse)
    {
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult(userResponse),
                _ => Task.FromResult(groupResponse));
    }

    [Fact]
    public async Task AddToGroupAsync_UserNotFound_ReturnsUserNotFound_NoModifyRequest()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        var result = await sut.AddToGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<UserNotFound>().Upn.ShouldBe(AliceUpn);

        await connection.DidNotReceive().ModifyAsync(
            Arg.Any<ModifyRequest>(),
            Arg.Any<CancellationToken>());

        int auditRows = await fixture.Context.AuditEntries.AsNoTracking().CountAsync();
        auditRows.ShouldBe(0);
    }

    [Fact]
    public async Task AddToGroupAsync_GroupNotFound_ReturnsGroupNotFound_NoModifyRequest()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        var result = await sut.AddToGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<GroupNotFound>().GroupDn.ShouldBe(GroupDn);

        await connection.DidNotReceive().ModifyAsync(
            Arg.Any<ModifyRequest>(),
            Arg.Any<CancellationToken>());

        int auditRows = await fixture.Context.AuditEntries.AsNoTracking().CountAsync();
        auditRows.ShouldBe(0);
    }

    [Fact]
    public async Task AddToGroupAsync_UserAlreadyInGroup_ReturnsAlreadyMember_NoModifyRequest()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn, AliceDn, OtherUserDn)]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        var result = await sut.AddToGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        AlreadyMember err = result.Error.ShouldBeOfType<AlreadyMember>();
        err.Upn.ShouldBe(AliceUpn);
        err.GroupDn.ShouldBe(GroupDn);

        await connection.DidNotReceive().ModifyAsync(
            Arg.Any<ModifyRequest>(),
            Arg.Any<CancellationToken>());

        int auditRows = await fixture.Context.AuditEntries.AsNoTracking().CountAsync();
        auditRows.ShouldBe(0);
    }

    [Fact]
    public async Task AddToGroupAsync_HappyPath_IssuesAddModifyRequestAgainstGroupDn_OnMemberAttribute()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn, OtherUserDn)]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        var result = await sut.AddToGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        await connection.Received(1).ModifyAsync(
            Arg.Is<ModifyRequest>(r =>
                r.DistinguishedName == GroupDn
                && r.Modifications.Count == 1
                && r.Modifications[0].Name == "member"
                && r.Modifications[0].Operation == DirectoryAttributeOperation.Add
                && r.Modifications[0].Count == 1
                && (string)r.Modifications[0][0] == AliceDn),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddToGroupAsync_HappyPath_EmitsExactlyOneAuditRow_WithAction_AddToGroup()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn)]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        await sut.AddToGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        var rows = await fixture.Context.AuditEntries.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);

        var row = rows[0];
        row.Action.ShouldBe("AddToGroup");
        row.TargetUpn.ShouldBe(AliceUpn);
        row.FieldName.ShouldBe("member");
        row.OldValue.ShouldBeNull();
        row.NewValue.ShouldBe(GroupDn);
        row.ActorUpn.ShouldBe(TestActor.Upn);
        row.Source.ShouldBe(TestActor.Source.ToString());
    }

    [Fact]
    public async Task AddToGroupAsync_FilterInputs_PassThroughLdapFilterEscape()
    {
        // The UPN contains an asterisk and parens — both RFC 4515 reserved.
        const string EvilUpn = "alice*(evil)@example.corp";
        // The group DN contains parens; they're legal in DNs but must be
        // RFC 4515-escaped when stuffed into a filter string.
        const string EvilGroupDn = "CN=Eng (Team),OU=Groups," + BaseDn;

        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        await sut.AddToGroupAsync(EvilUpn, EvilGroupDn, CancellationToken.None);

        // First call is the user lookup — UPN must be escaped.
        await connection.Received().SearchAsync(
            Arg.Is<SearchRequest>(r =>
                ((string)r.Filter).Contains("alice\\2a\\28evil\\29@example.corp", StringComparison.Ordinal)
                && !((string)r.Filter).Contains("alice*(evil)", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveFromGroupAsync_UserNotMember_ReturnsNotAMember_NoModifyRequest()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn, OtherUserDn)]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        var result = await sut.RemoveFromGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        NotAMember err = result.Error.ShouldBeOfType<NotAMember>();
        err.Upn.ShouldBe(AliceUpn);
        err.GroupDn.ShouldBe(GroupDn);

        await connection.DidNotReceive().ModifyAsync(
            Arg.Any<ModifyRequest>(),
            Arg.Any<CancellationToken>());

        int auditRows = await fixture.Context.AuditEntries.AsNoTracking().CountAsync();
        auditRows.ShouldBe(0);
    }

    [Fact]
    public async Task RemoveFromGroupAsync_HappyPath_IssuesDeleteModifyRequestAgainstGroupDn_OnMemberAttribute()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn, AliceDn, OtherUserDn)]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        var result = await sut.RemoveFromGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        await connection.Received(1).ModifyAsync(
            Arg.Is<ModifyRequest>(r =>
                r.DistinguishedName == GroupDn
                && r.Modifications.Count == 1
                && r.Modifications[0].Name == "member"
                && r.Modifications[0].Operation == DirectoryAttributeOperation.Delete
                && r.Modifications[0].Count == 1
                && (string)r.Modifications[0][0] == AliceDn),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveFromGroupAsync_HappyPath_EmitsExactlyOneAuditRow_WithAction_RemoveFromGroup()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        StubSearchSequence(
            connection,
            userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
            groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn, AliceDn)]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        var audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(connection, audit, fixture.CurrentActor);

        await sut.RemoveFromGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

        var rows = await fixture.Context.AuditEntries.AsNoTracking().ToListAsync();
        rows.Count.ShouldBe(1);

        var row = rows[0];
        row.Action.ShouldBe("RemoveFromGroup");
        row.TargetUpn.ShouldBe(AliceUpn);
        row.FieldName.ShouldBe("member");
        row.OldValue.ShouldBe(GroupDn);
        row.NewValue.ShouldBeNull();
        row.ActorUpn.ShouldBe(TestActor.Upn);
        row.Source.ShouldBe(TestActor.Source.ToString());
    }

    [Fact]
    public async Task BothMethods_ModifyTheGroupObject_NotTheUserObject()
    {
        // Add path
        {
            IAdConnection connection = Substitute.For<IAdConnection>();
            StubSearchSequence(
                connection,
                userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
                groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn)]));

            using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
            var audit = new AuditService(fixture.Context, fixture.CurrentActor);
            AdService sut = BuildService(connection, audit, fixture.CurrentActor);

            await sut.AddToGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

            await connection.Received(1).ModifyAsync(
                Arg.Is<ModifyRequest>(r => r.DistinguishedName == GroupDn),
                Arg.Any<CancellationToken>());

            await connection.DidNotReceive().ModifyAsync(
                Arg.Is<ModifyRequest>(r => r.DistinguishedName == AliceDn),
                Arg.Any<CancellationToken>());
        }

        // Remove path
        {
            IAdConnection connection = Substitute.For<IAdConnection>();
            StubSearchSequence(
                connection,
                userResponse: SearchResultEntryBuilder.BuildResponse([BuildUserEntry(AliceDn)]),
                groupResponse: SearchResultEntryBuilder.BuildResponse([BuildGroupEntry(GroupDn, AliceDn)]));

            using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
            var audit = new AuditService(fixture.Context, fixture.CurrentActor);
            AdService sut = BuildService(connection, audit, fixture.CurrentActor);

            await sut.RemoveFromGroupAsync(AliceUpn, GroupDn, CancellationToken.None);

            await connection.Received(1).ModifyAsync(
                Arg.Is<ModifyRequest>(r => r.DistinguishedName == GroupDn),
                Arg.Any<CancellationToken>());

            await connection.DidNotReceive().ModifyAsync(
                Arg.Is<ModifyRequest>(r => r.DistinguishedName == AliceDn),
                Arg.Any<CancellationToken>());
        }
    }
}
