using System.DirectoryServices.Protocols;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

/// <summary>
/// M1.4 cross-store create-user tests. Mock <see cref="IAdConnection"/>
/// for the AD side; real <see cref="AttributeService"/> /
/// <see cref="AuditService"/> / <see cref="ReconciliationQueueService"/>
/// over the SQLite-in-memory fixture for the sidecar side.
/// </summary>
public sealed class AdServiceCreateTests : IDisposable
{
    private const string BaseDn = "DC=example,DC=corp";
    private const string AllowedOu = "OU=Engineering,OU=People,DC=example,DC=corp";
    private const string Password = "Hunter2!CorrectHorseBatteryStaple";

    private readonly SqliteDbContextFixture _fixture = new(
        new StubCurrentActor(new Actor("admin@example.corp", ActorSource.Web)));

    [Fact]
    public async Task CreateAsync_OuNotInWhitelist_ReturnsOuNotAllowed_NoAdInteraction()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.Port.Returns(636);

        AdService sut = BuildService(connection);

        NewUserDto dto = NewDto("alice@example.corp", "OU=Disallowed,DC=example,DC=corp");

        Result<AdUser, CreateUserError> result = await sut.CreateAsync(dto, Password, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<CreateUserError.OuNotAllowed>()
            .OuPath.ShouldBe("OU=Disallowed,DC=example,DC=corp");

        // No AD interaction whatsoever — not even the uniqueness check.
        await connection.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().ModifyAsync(default!, default);
    }

    [Fact]
    public async Task CreateAsync_ExistingUpn_ReturnsUpnAlreadyExists_NoAdWrite()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.Port.Returns(636);

        // Pre-existing UPN: uniqueness search returns one entry.
        SearchResultEntry existing = SearchResultEntryBuilder.Build(
            $"CN=Alice,{AllowedOu}",
            new Dictionary<string, object?>
            {
                ["userPrincipalName"] = "alice@example.corp",
            });

        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([existing])));

        AdService sut = BuildService(connection);

        NewUserDto dto = NewDto("alice@example.corp", AllowedOu);

        Result<AdUser, CreateUserError> result = await sut.CreateAsync(dto, Password, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBeOfType<CreateUserError.UpnAlreadyExists>()
            .Upn.ShouldBe("alice@example.corp");

        await connection.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().ModifyAsync(default!, default);
    }

    [Fact]
    public async Task CreateAsync_NonLdapsConnection_ThrowsLdapsRequiredException()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.Port.Returns(389); // plain LDAP

        AdService sut = BuildService(connection);

        NewUserDto dto = NewDto("alice@example.corp", AllowedOu);

        await Should.ThrowAsync<LdapsRequiredException>(async () =>
            await sut.CreateAsync(dto, Password, CancellationToken.None));

        // The LDAPS guard fires before the uniqueness search.
        await connection.DidNotReceiveWithAnyArgs().SearchAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await connection.DidNotReceiveWithAnyArgs().ModifyAsync(default!, default);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_AdAndSidecarWritten_AdUserReturned()
    {
        IAdConnection connection = BuildLdapsConnectionWithNoExistingUser();

        AdService sut = BuildService(connection);

        NewUserDto dto = NewDto("alice@example.corp", AllowedOu) with
        {
            CostCenter = "CC-42",
            ContractType = "Permanent",
            EmployeeId = "EMP-001",
        };

        Result<AdUser, CreateUserError> result = await sut.CreateAsync(dto, Password, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        AdUser created = result.Value!;
        created.Upn.ShouldBe("alice@example.corp");
        created.SamAccountName.ShouldBe("alice");
        created.OuPath.ShouldBe(AllowedOu);
        created.Dn.ShouldBe($"CN={dto.DisplayName},{AllowedOu}");
        created.Enabled.ShouldBeTrue();

        // AD: exactly one Add and one Modify.
        await connection.Received(1).AddAsync(Arg.Any<AddRequest>(), Arg.Any<CancellationToken>());
        await connection.Received(1).ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>());

        // Sidecar row was written end-to-end via the real AttributeService.
        UserAttributes? sidecar = await _fixture.Context.UserAttributes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Upn == dto.Upn, CancellationToken.None);
        sidecar.ShouldNotBeNull();
        sidecar!.CostCenter.ShouldBe("CC-42");
        sidecar.ContractType.ShouldBe("Permanent");
        sidecar.EmployeeId.ShouldBe("EMP-001");

        // No reconciliation row on the happy path.
        (await _fixture.Context.ReconciliationQueue.CountAsync(CancellationToken.None)).ShouldBe(0);
    }

    [Fact]
    public async Task CreateAsync_HappyPath_ExactlyOneModifyRequestForPasswordAndUacFields()
    {
        IAdConnection connection = BuildLdapsConnectionWithNoExistingUser();

        AdService sut = BuildService(connection);

        NewUserDto dto = NewDto("bob@example.corp", AllowedOu);

        await sut.CreateAsync(dto, Password, CancellationToken.None);

        // The one Modify must carry exactly the three target attributes.
        await connection.Received(1).ModifyAsync(
            Arg.Is<ModifyRequest>(r =>
                r.Modifications.Count == 3
                && HasModification(r, "unicodePwd")
                && HasModification(r, "pwdLastSet")
                && HasModification(r, "userAccountControl")
                && PwdLastSetIsZero(r)
                && UacIsNormalEnabled(r)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_SidecarFailure_AuditPartialStateWritten_ReconciliationQueueRowInserted_ReturnsPartialSuccess()
    {
        IAdConnection connection = BuildLdapsConnectionWithNoExistingUser();

        AdService sut = BuildService(connection);

        // Force a sidecar conflict end-to-end: seed an existing row for the
        // target UPN. AttributeService.UpsertAsync with ifMatchRowVersion=null
        // against an existing row returns a ConcurrencyConflict, which the
        // service must surface as PartialSuccess.
        _fixture.Context.UserAttributes.Add(new UserAttributes
        {
            Upn = "carol@example.corp",
            CostCenter = "CC-OLD",
            RowVersion = Guid.NewGuid(),
        });
        await _fixture.Context.SaveChangesAsync(CancellationToken.None);

        // We do NOT want the audit interceptor to record the upsert seed
        // above as a normal audit row that would confuse the assertion below;
        // clear what's there so the partial-state row stands out.
        _fixture.Context.AuditEntries.RemoveRange(_fixture.Context.AuditEntries);
        await _fixture.Context.SaveChangesAsync(CancellationToken.None);

        NewUserDto dto = NewDto("carol@example.corp", AllowedOu) with
        {
            CostCenter = "CC-NEW",
            ContractType = "Contractor",
            EmployeeId = "EMP-002",
        };

        Result<AdUser, CreateUserError> result = await sut.CreateAsync(dto, Password, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        var partial = result.Error.ShouldBeOfType<CreateUserError.PartialSuccess>();
        partial.User.Upn.ShouldBe("carol@example.corp");
        partial.SidecarFailureReason.ShouldNotBeNullOrWhiteSpace();

        // AD side: the AD object WAS created — that's the whole point of
        // partial state — and there was no rollback DeleteAsync.
        await connection.Received(1).AddAsync(Arg.Any<AddRequest>(), Arg.Any<CancellationToken>());
        await connection.Received(1).ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>());
        await connection.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);

        // Sidecar row is unchanged (the seeded value).
        UserAttributes? sidecar = await _fixture.Context.UserAttributes
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Upn == dto.Upn, CancellationToken.None);
        sidecar.ShouldNotBeNull();
        sidecar!.CostCenter.ShouldBe("CC-OLD");

        // Reconciliation row inserted.
        ReconciliationQueue? queueRow = await _fixture.Context.ReconciliationQueue
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.TargetUpn == dto.Upn, CancellationToken.None);
        queueRow.ShouldNotBeNull();
        queueRow!.Operation.ShouldBe("CreateUser-SidecarMissing");
        queueRow.Status.ShouldBe(ReconciliationStatus.Open);
        // Payload is JSON containing the sidecar fields.
        queueRow.Payload.ShouldContain("\"Upn\":\"carol@example.corp\"");
        queueRow.Payload.ShouldContain("\"CostCenter\":\"CC-NEW\"");
        queueRow.Payload.ShouldContain("\"ContractType\":\"Contractor\"");
        queueRow.Payload.ShouldContain("\"EmployeeId\":\"EMP-002\"");

        // Audit row flagging the partial state.
        AuditEntry? auditRow = await _fixture.Context.AuditEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Action == "CreateUser-PartialState", CancellationToken.None);
        auditRow.ShouldNotBeNull();
        auditRow!.TargetUpn.ShouldBe(dto.Upn);
        auditRow.NewValue.ShouldBe("AD created; sidecar missing");
        auditRow.ActorUpn.ShouldBe("admin@example.corp"); // from the fixture's StubCurrentActor
        auditRow.Source.ShouldBe(ActorSource.Web.ToString());
    }

    [Fact]
    public async Task CreateAsync_Password_NeverAppearsInAuditOrLog()
    {
        IAdConnection connection = BuildLdapsConnectionWithNoExistingUser();

        // Seed a sidecar row to force partial state — that exercises the
        // audit / reconciliation write paths so we can assert no log
        // statement and no audit column carries the password.
        _fixture.Context.UserAttributes.Add(new UserAttributes
        {
            Upn = "dave@example.corp",
            CostCenter = "CC-OLD",
            RowVersion = Guid.NewGuid(),
        });
        await _fixture.Context.SaveChangesAsync(CancellationToken.None);
        _fixture.Context.AuditEntries.RemoveRange(_fixture.Context.AuditEntries);
        await _fixture.Context.SaveChangesAsync(CancellationToken.None);

        CapturingLoggerProvider provider = new();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        ILogger<AdService> logger = loggerFactory.CreateLogger<AdService>();

        AdService sut = BuildService(connection, logger);

        NewUserDto dto = NewDto("dave@example.corp", AllowedOu);

        await sut.CreateAsync(dto, Password, CancellationToken.None);

        // No captured log line contains the password substring.
        foreach (string line in provider.Lines)
        {
            line.ShouldNotContain(Password);
        }

        // No audit row carries the password in any string column.
        var auditRows = await _fixture.Context.AuditEntries.AsNoTracking().ToListAsync(CancellationToken.None);
        foreach (AuditEntry row in auditRows)
        {
            (row.OldValue ?? string.Empty).ShouldNotContain(Password);
            (row.NewValue ?? string.Empty).ShouldNotContain(Password);
            row.FieldName.ShouldNotContain(Password);
            row.Action.ShouldNotContain(Password);
            row.TargetUpn.ShouldNotContain(Password);
            row.ActorUpn.ShouldNotContain(Password);
            (row.Reason ?? string.Empty).ShouldNotContain(Password);
        }

        // No reconciliation payload carries the password.
        var queueRows = await _fixture.Context.ReconciliationQueue.AsNoTracking().ToListAsync(CancellationToken.None);
        foreach (ReconciliationQueue row in queueRows)
        {
            row.Payload.ShouldNotContain(Password);
        }
    }

    private AdService BuildService(IAdConnection connection, ILogger<AdService>? logger = null)
    {
        IOptions<AdOptions> options = Options.Create(new AdOptions
        {
            BaseDn = BaseDn,
            Port = 636,
            AllowedOus = [AllowedOu, "OU=Sales,OU=People,DC=example,DC=corp"],
        });

        IAttributeService attributeService = new AttributeService(_fixture.Context);
        IAuditService auditService = new AuditService(_fixture.Context, _fixture.CurrentActor, _fixture.TimeProvider);
        IReconciliationQueueService reconciliationQueue = new ReconciliationQueueService(_fixture.Context, _fixture.TimeProvider);

        return new AdService(
            connection,
            options,
            logger ?? NullLogger<AdService>.Instance,
            attributeService,
            auditService,
            reconciliationQueue);
    }

    private static IAdConnection BuildLdapsConnectionWithNoExistingUser()
    {
        IAdConnection connection = Substitute.For<IAdConnection>();
        connection.Port.Returns(636);
        connection.SearchAsync(Arg.Any<SearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(SearchResultEntryBuilder.BuildResponse([])));
        connection.AddAsync(Arg.Any<AddRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(CreateResponse<AddResponse>()));
        connection.ModifyAsync(Arg.Any<ModifyRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(CreateResponse<ModifyResponse>()));
        return connection;
    }

    private static NewUserDto NewDto(string upn, string ouPath)
    {
        string local = upn.Split('@')[0];
        return new NewUserDto(
            Upn: upn,
            SamAccountName: local,
            GivenName: char.ToUpperInvariant(local[0]) + local[1..],
            Surname: "Doe",
            DisplayName: $"{char.ToUpperInvariant(local[0]) + local[1..]} Doe",
            OuPath: ouPath,
            Department: "Engineering",
            ManagerDn: null,
            CostCenter: null,
            ContractType: null,
            EmployeeId: null);
    }

    private static bool HasModification(ModifyRequest request, string attributeName)
    {
        foreach (DirectoryAttributeModification mod in request.Modifications)
        {
            if (string.Equals(mod.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PwdLastSetIsZero(ModifyRequest request)
    {
        foreach (DirectoryAttributeModification mod in request.Modifications)
        {
            if (string.Equals(mod.Name, "pwdLastSet", StringComparison.OrdinalIgnoreCase))
            {
                return mod.Count == 1 && string.Equals(mod[0] as string, "0", StringComparison.Ordinal);
            }
        }

        return false;
    }

    private static bool UacIsNormalEnabled(ModifyRequest request)
    {
        foreach (DirectoryAttributeModification mod in request.Modifications)
        {
            if (string.Equals(mod.Name, "userAccountControl", StringComparison.OrdinalIgnoreCase))
            {
                return mod.Count == 1 && string.Equals(mod[0] as string, "512", StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// Build a <see cref="DirectoryResponse"/> via the BCL's internal
    /// 5-arg constructor. Same reflection seam as the existing
    /// <c>AdConnectionTests.CreateResponse</c> helper; copied locally
    /// rather than promoted to a fixture because no other test under
    /// <c>Services/</c> needs it yet.
    /// </summary>
    private static TResponse CreateResponse<TResponse>()
        where TResponse : DirectoryResponse
    {
        ConstructorInfo? ctor = typeof(TResponse).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(DirectoryControl[]), typeof(ResultCode), typeof(string), typeof(Uri[])],
            modifiers: null);
        if (ctor is null)
        {
            throw new InvalidOperationException($"No 5-arg internal constructor on {typeof(TResponse).Name}.");
        }

        return (TResponse)ctor.Invoke([null!, Array.Empty<DirectoryControl>(), ResultCode.Success, null!, Array.Empty<Uri>()]);
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        public IReadOnlyList<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_lines);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<string> _lines;
            public CapturingLogger(List<string> lines) => _lines = lines;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                string line = formatter(state, exception);
                if (exception is not null)
                {
                    line = string.Concat(line, " | ", exception.ToString());
                }

                _lines.Add(line);
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose()
                {
                }
            }
        }
    }
}
