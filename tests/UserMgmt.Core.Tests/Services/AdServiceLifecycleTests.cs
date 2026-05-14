using System.DirectoryServices.Protocols;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Auth;
using UserMgmt.Core.Common;
using UserMgmt.Core.Ldap;
using UserMgmt.Core.Services;
using UserMgmt.Core.Tests.Fixtures;
using UserMgmt.Data.Entities;
using UserMgmt.Data.Services;

namespace UserMgmt.Core.Tests.Services;

public sealed class AdServiceLifecycleTests
{
    private const string BaseDn = "DC=example,DC=corp";
    private const string Upn = "alice@example.corp";
    private const string UserDn = "CN=Alice,OU=People,DC=example,DC=corp";
    private static readonly Actor TestActor = new("admin@example.corp", ActorSource.Web);

    private static AdService BuildService(
        IAdConnection connection,
        IAuditService auditService,
        Microsoft.Extensions.Logging.ILogger<AdService>? logger = null,
        int port = 636)
    {
        IOptions<AdOptions> options = Options.Create(new AdOptions
        {
            BaseDn = BaseDn,
            Port = port,
        });
        return new AdService(
            connection,
            options,
            logger ?? NullLogger<AdService>.Instance,
            auditService);
    }

    private static SearchResponse BuildUserSearchResponse(string uacRaw)
    {
        SearchResultEntry entry = SearchResultEntryBuilder.Build(
            UserDn,
            new Dictionary<string, object?>
            {
                ["userAccountControl"] = uacRaw,
            });
        return SearchResultEntryBuilder.BuildResponse([entry]);
    }

    // -----------------------------------------------------------------
    // SetEnabledAsync — happy paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task SetEnabledAsync_EnableTransition_FlipsAccountDisableBit_UsingDeleteOldAddNewCas()
    {
        StubConnection conn = StubConnection.For(port: 636);
        // Start state: disabled (514 = 512 NORMAL_ACCOUNT | 2 ACCOUNTDISABLE).
        conn.SetSearchResponse(BuildUserSearchResponse("514"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, EnableUserError> result =
            await sut.SetEnabledAsync(Upn, enabled: true, reason: null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        ModifyRequest sent = conn.LastModifyRequest.ShouldNotBeNull();
        sent.DistinguishedName.ShouldBe(UserDn);
        sent.Modifications.Count.ShouldBe(2);

        // CAS pattern: delete-old THEN add-new on userAccountControl.
        DirectoryAttributeModification deleteOld = sent.Modifications[0];
        deleteOld.Operation.ShouldBe(DirectoryAttributeOperation.Delete);
        deleteOld.Name.ShouldBe("userAccountControl");
        ReadFirstString(deleteOld).ShouldBe("514");

        DirectoryAttributeModification addNew = sent.Modifications[1];
        addNew.Operation.ShouldBe(DirectoryAttributeOperation.Add);
        addNew.Name.ShouldBe("userAccountControl");
        // Enable clears the ACCOUNTDISABLE bit: 514 & ~2 = 512.
        ReadFirstString(addNew).ShouldBe("512");
    }

    [Fact]
    public async Task SetEnabledAsync_DisableTransition_FlipsAccountDisableBit_UsingDeleteOldAddNewCas()
    {
        StubConnection conn = StubConnection.For(port: 636);
        // Start state: enabled (512).
        conn.SetSearchResponse(BuildUserSearchResponse("512"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, EnableUserError> result =
            await sut.SetEnabledAsync(Upn, enabled: false, reason: null, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        ModifyRequest sent = conn.LastModifyRequest.ShouldNotBeNull();
        sent.Modifications.Count.ShouldBe(2);

        DirectoryAttributeModification deleteOld = sent.Modifications[0];
        deleteOld.Operation.ShouldBe(DirectoryAttributeOperation.Delete);
        ReadFirstString(deleteOld).ShouldBe("512");

        DirectoryAttributeModification addNew = sent.Modifications[1];
        addNew.Operation.ShouldBe(DirectoryAttributeOperation.Add);
        // Disable sets the ACCOUNTDISABLE bit: 512 | 2 = 514.
        ReadFirstString(addNew).ShouldBe("514");
    }

    [Fact]
    public async Task SetEnabledAsync_DisableWithReasonStale_AuditRowHasReason()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(BuildUserSearchResponse("512"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, EnableUserError> result =
            await sut.SetEnabledAsync(Upn, enabled: false, reason: AuditReason.Stale, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        var row = await fixture.Context.AuditEntries.AsNoTracking().SingleAsync();
        row.Action.ShouldBe("Disable");
        row.Reason.ShouldBe(AuditReason.Stale);
        row.TargetUpn.ShouldBe(Upn);
        row.FieldName.ShouldBe("userAccountControl");
        row.OldValue.ShouldBe("512");
        row.NewValue.ShouldBe("514");
        row.ActorUpn.ShouldBe(TestActor.Upn);
        row.Source.ShouldBe(TestActor.Source.ToString());
    }

    [Fact]
    public async Task SetEnabledAsync_DisableWithInvalidReason_ReturnsInvalidReason_NoAdInteraction()
    {
        StubConnection conn = StubConnection.For(port: 636);

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, EnableUserError> result =
            await sut.SetEnabledAsync(Upn, enabled: false, reason: "MysteryReason", CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<EnableUserError.InvalidReason>()
            .Reason.ShouldBe("MysteryReason");

        // No AD traffic and no audit row.
        conn.SearchCount.ShouldBe(0);
        conn.ModifyCount.ShouldBe(0);
        (await fixture.Context.AuditEntries.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task SetEnabledAsync_EmitsExactlyOneAuditRow()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(BuildUserSearchResponse("512"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        await sut.SetEnabledAsync(Upn, enabled: false, reason: AuditReason.Termination, CancellationToken.None);

        (await fixture.Context.AuditEntries.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task SetEnabledAsync_UnknownUpn_ReturnsUserNotFound_AndNoAudit()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(SearchResultEntryBuilder.BuildResponse([]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, EnableUserError> result =
            await sut.SetEnabledAsync("ghost@example.corp", enabled: false, reason: null, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<EnableUserError.UserNotFound>()
            .Upn.ShouldBe("ghost@example.corp");

        conn.ModifyCount.ShouldBe(0);
        (await fixture.Context.AuditEntries.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task SetEnabledAsync_CasFailureFromServer_ReturnsConcurrencyConflict_AndNoAudit()
    {
        StubConnection conn = StubConnection.For(port: 636);

        // First search returns 514 (disabled), second (re-read after failure)
        // returns 530 — simulating a concurrent writer.
        SearchResponse first = BuildUserSearchResponse("514");
        SearchResponse second = BuildUserSearchResponse("530");
        conn.SetSearchResponses([first, second]);

        // Modify fails with NoSuchAttribute — the delete-old value
        // doesn't exist on the server because the value has drifted.
        var failedResponse = DirectoryResponseBuilder.Build<ModifyResponse>(ResultCode.NoSuchAttribute);
        conn.SetModifyException(new DirectoryOperationException(failedResponse));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, EnableUserError> result =
            await sut.SetEnabledAsync(Upn, enabled: true, reason: null, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        var conflict = result.Error.ShouldBeOfType<EnableUserError.ConcurrencyConflict>();
        conflict.Attribute.ShouldBe("userAccountControl");
        conflict.CurrentValue.ShouldBe("530");

        (await fixture.Context.AuditEntries.CountAsync()).ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // ResetPasswordAsync — LDAPS enforcement
    // -----------------------------------------------------------------

    [Fact]
    public async Task ResetPasswordAsync_NonLdapsConnection_ThrowsLdapsRequiredException()
    {
        StubConnection conn = StubConnection.For(port: 389);

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit, port: 389);

        await Should.ThrowAsync<LdapsRequiredException>(async () =>
            await sut.ResetPasswordAsync(Upn, "SomePassw0rd!", CancellationToken.None));

        // The refusal happens BEFORE any AD traffic or audit write.
        conn.SearchCount.ShouldBe(0);
        conn.ModifyCount.ShouldBe(0);
        (await fixture.Context.AuditEntries.CountAsync()).ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // ResetPasswordAsync — happy paths
    // -----------------------------------------------------------------

    [Fact]
    public async Task ResetPasswordAsync_LdapsConnection_WritesPasswordPwdLastSetAndUacInSingleModifyRequest()
    {
        StubConnection conn = StubConnection.For(port: 636);
        // 514 = disabled; reset will clear the disable bit.
        conn.SetSearchResponse(BuildUserSearchResponse("514"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, ResetPasswordError> result =
            await sut.ResetPasswordAsync(Upn, "Hunter2!Hunter2!", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        ModifyRequest sent = conn.LastModifyRequest.ShouldNotBeNull();
        sent.DistinguishedName.ShouldBe(UserDn);

        // Exactly three modifications, all in one ModifyRequest, all Replace.
        sent.Modifications.Count.ShouldBe(3);

        DirectoryAttributeModification pwd = sent.Modifications
            .Cast<DirectoryAttributeModification>()
            .Single(m => m.Name == "unicodePwd");
        pwd.Operation.ShouldBe(DirectoryAttributeOperation.Replace);

        DirectoryAttributeModification pwdLast = sent.Modifications
            .Cast<DirectoryAttributeModification>()
            .Single(m => m.Name == "pwdLastSet");
        pwdLast.Operation.ShouldBe(DirectoryAttributeOperation.Replace);
        ReadFirstString(pwdLast).ShouldBe("0");

        DirectoryAttributeModification uac = sent.Modifications
            .Cast<DirectoryAttributeModification>()
            .Single(m => m.Name == "userAccountControl");
        uac.Operation.ShouldBe(DirectoryAttributeOperation.Replace);
        // 514 with bit 0x2 cleared = 512.
        ReadFirstString(uac).ShouldBe("512");

        // Exactly one ModifyRequest was sent.
        conn.ModifyCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResetPasswordAsync_PasswordEncodedAsUtf16LeQuotedString()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(BuildUserSearchResponse("512"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        const string password = "P@ssw0rdZZ!";
        await sut.ResetPasswordAsync(Upn, password, CancellationToken.None);

        ModifyRequest sent = conn.LastModifyRequest.ShouldNotBeNull();
        DirectoryAttributeModification pwd = sent.Modifications
            .Cast<DirectoryAttributeModification>()
            .Single(m => m.Name == "unicodePwd");

        // unicodePwd values must be the new password wrapped in U+0022 quotes,
        // encoded as UTF-16LE — MS-ADTS §3.1.1.3.1.5.
        byte[] expected = Encoding.Unicode.GetBytes($"\"{password}\"");
        pwd.Count.ShouldBe(1);
        byte[] actual = (byte[])pwd[0]!;
        actual.ShouldBe(expected);
    }

    [Fact]
    public async Task ResetPasswordAsync_AuditRowHasNoPasswordInOldOrNewValue()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(BuildUserSearchResponse("512"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        const string password = "DoNotLeakMe!9!";
        await sut.ResetPasswordAsync(Upn, password, CancellationToken.None);

        var row = await fixture.Context.AuditEntries.AsNoTracking().SingleAsync();
        row.Action.ShouldBe("ResetPassword");
        row.TargetUpn.ShouldBe(Upn);
        row.FieldName.ShouldBe("unicodePwd");
        row.OldValue.ShouldBeNull();
        row.NewValue.ShouldBeNull();
        row.ActorUpn.ShouldBe(TestActor.Upn);

        // The password must not appear ANYWHERE in the persisted audit row.
        AssertNoPasswordSubstring(row, password);
    }

    [Fact]
    public async Task ResetPasswordAsync_PasswordNeverAppearsInCapturedLogOutput()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(BuildUserSearchResponse("512"));
        conn.SetModifyResponse(DirectoryResponseBuilder.BuildSuccess<ModifyResponse>());

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        CapturingLogger<AdService> logger = new();
        AdService sut = BuildService(conn, audit, logger);

        const string password = "LeakProbe-XYZ42!";
        await sut.ResetPasswordAsync(Upn, password, CancellationToken.None);

        // Every captured message is scanned. The probe substring is
        // distinctive enough that any accidental interpolation would be
        // unmistakable.
        foreach (string captured in logger.Captured)
        {
            captured.ShouldNotContain(password);
        }
    }

    [Fact]
    public async Task ResetPasswordAsync_UnknownUpn_ReturnsUserNotFound_AndNoAudit()
    {
        StubConnection conn = StubConnection.For(port: 636);
        conn.SetSearchResponse(SearchResultEntryBuilder.BuildResponse([]));

        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        Result<Unit, ResetPasswordError> result =
            await sut.ResetPasswordAsync("ghost@example.corp", "AnyPw1!AnyPw1!", CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<ResetPasswordError.UserNotFound>()
            .Upn.ShouldBe("ghost@example.corp");

        conn.ModifyCount.ShouldBe(0);
        (await fixture.Context.AuditEntries.CountAsync()).ShouldBe(0);
    }

    // -----------------------------------------------------------------
    // Input validation
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SetEnabledAsync_BlankUpn_Throws(string? upn)
    {
        StubConnection conn = StubConnection.For(port: 636);
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await sut.SetEnabledAsync(upn!, enabled: true, reason: null, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResetPasswordAsync_BlankUpn_Throws(string? upn)
    {
        StubConnection conn = StubConnection.For(port: 636);
        using var fixture = new SqliteDbContextFixture(new StubCurrentActor(TestActor));
        IAuditService audit = new AuditService(fixture.Context, fixture.CurrentActor);
        AdService sut = BuildService(conn, audit);

        await Should.ThrowAsync<ArgumentException>(async () =>
            await sut.ResetPasswordAsync(upn!, "Whatever1!", CancellationToken.None));
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Read the first attribute value from a <see cref="DirectoryAttributeModification"/>
    /// as a string. Modifications carry an indexable value collection
    /// inherited from <see cref="DirectoryAttribute"/>.
    /// </summary>
    private static string ReadFirstString(DirectoryAttributeModification mod)
    {
        mod.Count.ShouldBe(1);
        object? value = mod[0];
        return value switch
        {
            string s => s,
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            _ => value?.ToString() ?? string.Empty,
        };
    }

    private static void AssertNoPasswordSubstring(AuditEntry row, string password)
    {
        row.OldValue?.ShouldNotContain(password);
        row.NewValue?.ShouldNotContain(password);
        row.FieldName.ShouldNotContain(password);
        row.Action.ShouldNotContain(password);
        row.Reason?.ShouldNotContain(password);
        row.Source.ShouldNotContain(password);
        row.ActorUpn.ShouldNotContain(password);
        row.TargetUpn.ShouldNotContain(password);
    }

    /// <summary>
    /// Hand-rolled <see cref="IAdConnection"/> double. NSubstitute would work
    /// for return-value matching, but recording the actual
    /// <see cref="ModifyRequest"/> for later assertion is awkward through
    /// the substitute surface — recording it directly keeps the assertions
    /// readable.
    /// </summary>
    private sealed class StubConnection : IAdConnection
    {
        private readonly Queue<SearchResponse> _searchResponses = new();
        private SearchResponse? _defaultSearchResponse;
        private ModifyResponse? _modifyResponse;
        private Exception? _modifyException;

        public static StubConnection For(int port) => new() { Port = port };

        public int Port { get; private init; }

        public int SearchCount { get; private set; }

        public int ModifyCount { get; private set; }

        public ModifyRequest? LastModifyRequest { get; private set; }

        public void SetSearchResponse(SearchResponse response) => _defaultSearchResponse = response;

        public void SetSearchResponses(IEnumerable<SearchResponse> responses)
        {
            foreach (var r in responses)
            {
                _searchResponses.Enqueue(r);
            }
        }

        public void SetModifyResponse(ModifyResponse response) => _modifyResponse = response;

        public void SetModifyException(Exception ex) => _modifyException = ex;

        public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            SearchCount++;
            if (_searchResponses.TryDequeue(out SearchResponse? next))
            {
                return Task.FromResult(next);
            }

            return Task.FromResult(_defaultSearchResponse
                ?? throw new InvalidOperationException("No SearchResponse configured on stub."));
        }

        public Task<AddResponse> AddAsync(AddRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ModifyResponse> ModifyAsync(ModifyRequest request, CancellationToken cancellationToken = default)
        {
            ModifyCount++;
            LastModifyRequest = request;
            if (_modifyException is not null)
            {
                throw _modifyException;
            }

            return Task.FromResult(_modifyResponse
                ?? throw new InvalidOperationException("No ModifyResponse configured on stub."));
        }

        public Task<DeleteResponse> DeleteAsync(DeleteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
