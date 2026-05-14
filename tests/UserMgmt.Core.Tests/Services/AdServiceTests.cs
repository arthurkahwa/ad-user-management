using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Domain;
using UserMgmt.Core.Ldap;
using UserMgmt.Core.Services;
using UserMgmt.Core.Tests.Fixtures;

namespace UserMgmt.Core.Tests.Services;

public sealed class AdServiceTests
{
    private const string BaseDn = "DC=example,DC=corp";

    private static AdService BuildService(IAdConnection connection, string baseDn = BaseDn)
    {
        IOptions<AdOptions> options = Options.Create(new AdOptions
        {
            BaseDn = baseDn,
            Port = 636,
        });
        return new AdService(connection, options, NullLogger<AdService>.Instance);
    }

    [Fact]
    public async Task SearchAsync_PassesFilterFragmentThroughLdapFilterEscape()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        // "Smith*" contains an asterisk that must be RFC 4515-escaped before
        // it reaches the filter string. If escape isn't applied, the resulting
        // filter contains an un-escaped wildcard and we'd inject directly into
        // the predicate. After escape, "*" becomes "\2a".
        await sut.SearchAsync("Smith*", page: 1, pageSize: 10);

        await connection.Received(1).SearchAsync(
            Arg.Is<SearchRequest>(r =>
                ((string)r.Filter).Contains("Smith\\2a", StringComparison.Ordinal)
                && !((string)r.Filter).Contains("Smith*", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_EscapesParenthesesInQuery()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        await sut.SearchAsync("(evil)", page: 1, pageSize: 10);

        await connection.Received(1).SearchAsync(
            Arg.Is<SearchRequest>(r =>
                ((string)r.Filter).Contains("\\28evil\\29", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_BuildsFilterWithObjectClassAndCommonAttributes()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        await sut.SearchAsync("alice", page: 1, pageSize: 10);

        await connection.Received(1).SearchAsync(
            Arg.Is<SearchRequest>(r =>
                ((string)r.Filter).Contains("objectCategory=person", StringComparison.Ordinal)
                && ((string)r.Filter).Contains("objectClass=user", StringComparison.Ordinal)
                && ((string)r.Filter).Contains("displayName=*alice*", StringComparison.Ordinal)
                && ((string)r.Filter).Contains("sAMAccountName=*alice*", StringComparison.Ordinal)
                && ((string)r.Filter).Contains("userPrincipalName=*alice*", StringComparison.Ordinal)
                && ((string)r.Filter).Contains("cn=*alice*", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_AddsPageResultRequestControlWithRequestedSize()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        await sut.SearchAsync("alice", page: 1, pageSize: 25);

        await connection.Received(1).SearchAsync(
            Arg.Is<SearchRequest>(r => r.Controls.OfType<PageResultRequestControl>().Any(c => c.PageSize == 25)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_ScopesSearchUnderBaseDnFromOptions()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection, baseDn: "OU=People,DC=corp,DC=example,DC=com");

        await sut.SearchAsync("anyone", page: 1, pageSize: 10);

        await connection.Received(1).SearchAsync(
            Arg.Is<SearchRequest>(r => r.DistinguishedName == "OU=People,DC=corp,DC=example,DC=com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchAsync_NoMatches_ReturnsEmptyPageWithTotalZero()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        Common.PagedResult<AdUser> page = await sut.SearchAsync("nobody", page: 1, pageSize: 10);

        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
        page.Page.ShouldBe(1);
        page.PageSize.ShouldBe(10);
    }

    [Fact]
    public async Task SearchAsync_PaginatesAcrossPageControlCookies_AndCountsTotal()
    {
        // Page 1: 3 entries, cookie present.
        SearchResultEntry e1 = SearchResultEntryBuilder.Build("CN=A1,OU=p," + BaseDn, BuildBasicAttrs("a1"));
        SearchResultEntry e2 = SearchResultEntryBuilder.Build("CN=A2,OU=p," + BaseDn, BuildBasicAttrs("a2"));
        SearchResultEntry e3 = SearchResultEntryBuilder.Build("CN=A3,OU=p," + BaseDn, BuildBasicAttrs("a3"));
        // Page 2: 2 more entries, empty cookie (end).
        SearchResultEntry e4 = SearchResultEntryBuilder.Build("CN=A4,OU=p," + BaseDn, BuildBasicAttrs("a4"));
        SearchResultEntry e5 = SearchResultEntryBuilder.Build("CN=A5,OU=p," + BaseDn, BuildBasicAttrs("a5"));

        SearchResponse first = SearchResultEntryBuilder.BuildResponse([e1, e2, e3], pageCookie: [1]);
        SearchResponse second = SearchResultEntryBuilder.BuildResponse([e4, e5], pageCookie: null);

        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(first), _ => Task.FromResult(second));

        AdService sut = BuildService(connection);

        Common.PagedResult<AdUser> page1 = await sut.SearchAsync("a", page: 1, pageSize: 3);

        page1.TotalCount.ShouldBe(5);
        page1.Items.Count.ShouldBe(3);
        page1.Items.Select(u => u.SamAccountName).ShouldBe(["a1", "a2", "a3"]);
    }

    [Fact]
    public async Task GetAsync_UnknownUpn_ReturnsNull_WhenServerReturnsNoEntries()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        AdUser? result = await sut.GetAsync("ghost@example.corp");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_UpnFound_MapsAllAttributesIntoAdUser()
    {
        Dictionary<string, object?> attrs = new()
        {
            ["sAMAccountName"] = "alice",
            ["userPrincipalName"] = "alice@example.corp",
            ["displayName"] = "Alice Smith",
            ["givenName"] = "Alice",
            ["sn"] = "Smith",
            ["department"] = "Engineering",
            ["manager"] = "CN=Bob,OU=Mgrs,DC=example,DC=corp",
            ["whenCreated"] = "20260101120000.0Z",
            ["lastLogonTimestamp"] = DateTime.SpecifyKind(new DateTime(2026, 5, 1, 9, 0, 0), DateTimeKind.Utc).ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["userAccountControl"] = "512", // enabled, normal user account
        };
        SearchResultEntry entry = SearchResultEntryBuilder.Build(
            "CN=Alice Smith,OU=Engineering,OU=People,DC=example,DC=corp",
            attrs);

        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([entry])));

        AdService sut = BuildService(connection);

        AdUser? result = await sut.GetAsync("alice@example.corp");

        result.ShouldNotBeNull();
        result.Upn.ShouldBe("alice@example.corp");
        result.SamAccountName.ShouldBe("alice");
        result.Dn.ShouldBe("CN=Alice Smith,OU=Engineering,OU=People,DC=example,DC=corp");
        result.DisplayName.ShouldBe("Alice Smith");
        result.GivenName.ShouldBe("Alice");
        result.Surname.ShouldBe("Smith");
        result.Department.ShouldBe("Engineering");
        result.ManagerDn.ShouldBe("CN=Bob,OU=Mgrs,DC=example,DC=corp");
        result.OuPath.ShouldBe("OU=Engineering,OU=People,DC=example,DC=corp");
        result.WhenCreated.ShouldBe(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        result.LastLogon.ShouldBe(new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc));
        result.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAsync_AccountDisableBitSet_MapsEnabledFalse()
    {
        SearchResultEntry entry = SearchResultEntryBuilder.Build(
            "CN=Disabled User,OU=p,DC=example,DC=corp",
            new Dictionary<string, object?>
            {
                ["sAMAccountName"] = "disabled",
                ["userPrincipalName"] = "disabled@example.corp",
                ["displayName"] = "Disabled User",
                ["whenCreated"] = "20260101120000.0Z",
                ["userAccountControl"] = "514", // 512 (NORMAL_ACCOUNT) | 2 (ACCOUNTDISABLE)
            });

        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([entry])));

        AdService sut = BuildService(connection);

        AdUser? result = await sut.GetAsync("disabled@example.corp");

        result.ShouldNotBeNull();
        result.Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task GetAsync_LastLogonZero_MapsToNull()
    {
        SearchResultEntry entry = SearchResultEntryBuilder.Build(
            "CN=Fresh,OU=p,DC=example,DC=corp",
            new Dictionary<string, object?>
            {
                ["sAMAccountName"] = "fresh",
                ["userPrincipalName"] = "fresh@example.corp",
                ["displayName"] = "Fresh User",
                ["whenCreated"] = "20260513094500.0Z",
                ["lastLogonTimestamp"] = "0",
                ["userAccountControl"] = "512",
            });

        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([entry])));

        AdService sut = BuildService(connection);

        AdUser? result = await sut.GetAsync("fresh@example.corp");

        result.ShouldNotBeNull();
        result.LastLogon.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_EscapesUpnInFilter()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));

        AdService sut = BuildService(connection);

        await sut.GetAsync("alice*@example.corp");

        await connection.Received(1).SearchAsync(
            Arg.Is<SearchRequest>(r =>
                ((string)r.Filter).Contains("alice\\2a@example.corp", StringComparison.Ordinal)
                && !((string)r.Filter).Contains("alice*@", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAsync_BlankUpn_Throws(string? upn)
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        AdService sut = BuildService(connection);

        await Should.ThrowAsync<ArgumentException>(async () => await sut.GetAsync(upn!));
    }

    [Fact]
    public async Task SearchAsync_NullQuery_Throws()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        AdService sut = BuildService(connection);

        await Should.ThrowAsync<ArgumentNullException>(async () => await sut.SearchAsync(null!, 1, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchAsync_NonPositivePage_Throws(int page)
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        AdService sut = BuildService(connection);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await sut.SearchAsync("q", page, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchAsync_NonPositivePageSize_Throws(int pageSize)
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        AdService sut = BuildService(connection);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await sut.SearchAsync("q", 1, pageSize));
    }

    private static Dictionary<string, object?> BuildBasicAttrs(string sam) =>
        new()
        {
            ["sAMAccountName"] = sam,
            ["userPrincipalName"] = $"{sam}@example.corp",
            ["displayName"] = sam,
            ["whenCreated"] = "20260513094500.0Z",
            ["userAccountControl"] = "512",
        };
}
