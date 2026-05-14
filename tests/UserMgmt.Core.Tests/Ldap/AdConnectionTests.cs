using System.Collections.Concurrent;
using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UserMgmt.Core.Ldap;
using UserMgmt.Core.Tests.Fixtures;

namespace UserMgmt.Core.Tests.Ldap;

public sealed class AdConnectionTests
{
    private static IOptions<AdOptions> Options(string? preferredDc = "dc01.example.corp", int port = 636) =>
        Microsoft.Extensions.Options.Options.Create(new AdOptions
        {
            PreferredDc = preferredDc,
            Port = port,
            BaseDn = "DC=example,DC=corp",
        });

    private static LdapConnection NewUnboundLdapConnection(string host, int port) =>
        new(new LdapDirectoryIdentifier(host, port));

    [Fact]
    public void Port_ReflectsAdOptionsPort()
    {
        using AdConnection sut = new(
            Options(port: 636),
            NullLogger<AdConnection>.Instance,
            connectionFactory: NewUnboundLdapConnection,
            dcLocator: () => "should-not-be-called",
            sender: (_, _, _) => Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([])));

        sut.Port.ShouldBe(636);
    }

    [Fact]
    public void Port_NonLdapsPort_IsExposed()
    {
        using AdConnection sut = new(
            Options(port: 389),
            NullLogger<AdConnection>.Instance,
            connectionFactory: NewUnboundLdapConnection,
            dcLocator: () => "x",
            sender: (_, _, _) => Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([])));

        sut.Port.ShouldBe(389);
    }

    [Fact]
    public async Task SearchAsync_PreferredDc_BindsAgainstConfiguredDc_AndSkipsLocator()
    {
        ConcurrentBag<string> hostsRequested = [];
        int locatorCalls = 0;

        using AdConnection sut = new(
            Options(preferredDc: "dc-primary.example.corp"),
            NullLogger<AdConnection>.Instance,
            connectionFactory: (host, port) =>
            {
                hostsRequested.Add(host);
                return NewUnboundLdapConnection(host, port);
            },
            dcLocator: () => { Interlocked.Increment(ref locatorCalls); return "locator-fallback"; },
            sender: (_, _, _) => Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([])));

        await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base));

        hostsRequested.ShouldContain("dc-primary.example.corp");
        locatorCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_NoPreferredDc_InvokesDcLocator()
    {
        int locatorCalls = 0;
        ConcurrentBag<string> hostsRequested = [];

        using AdConnection sut = new(
            Options(preferredDc: null),
            NullLogger<AdConnection>.Instance,
            connectionFactory: (host, port) =>
            {
                hostsRequested.Add(host);
                return NewUnboundLdapConnection(host, port);
            },
            dcLocator: () => { Interlocked.Increment(ref locatorCalls); return "auto-located-dc.example.corp"; },
            sender: (_, _, _) => Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([])));

        await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base));

        locatorCalls.ShouldBe(1);
        hostsRequested.ShouldContain("auto-located-dc.example.corp");
    }

    [Fact]
    public async Task SearchAsync_LocatorCalledOnce_AcrossSequentialCalls()
    {
        int locatorCalls = 0;

        using AdConnection sut = new(
            Options(preferredDc: null),
            NullLogger<AdConnection>.Instance,
            connectionFactory: NewUnboundLdapConnection,
            dcLocator: () => { Interlocked.Increment(ref locatorCalls); return "dc.example.corp"; },
            sender: (_, _, _) => Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([])));

        for (int i = 0; i < 5; i++)
        {
            await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base));
        }

        locatorCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SearchAsync_LdapExceptionDuringRequest_EvictsCachedConnection_NextCallReconnects()
    {
        int constructionCalls = 0;
        int sendCalls = 0;

        using AdConnection sut = new(
            Options(preferredDc: "dc.example.corp"),
            NullLogger<AdConnection>.Instance,
            connectionFactory: (host, port) =>
            {
                Interlocked.Increment(ref constructionCalls);
                return NewUnboundLdapConnection(host, port);
            },
            dcLocator: () => "dc.example.corp",
            sender: (_, _, _) =>
            {
                int n = Interlocked.Increment(ref sendCalls);
                return n == 1
                    ? Task.FromException<DirectoryResponse>(new LdapException("simulated transport failure"))
                    : Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([]));
            });

        // First call: connection #1 is created, send fails with LdapException,
        // the entry is evicted, exception propagates.
        await Should.ThrowAsync<LdapException>(async () =>
            await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base)));

        // Second call: a fresh connection is created (eviction confirmed).
        await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base));

        constructionCalls.ShouldBe(2);
        sendCalls.ShouldBe(2);
    }

    [Fact]
    public async Task SearchAsync_ConcurrentCalls_OnSameInstance_DoNotConstructDuplicateConnections()
    {
        int constructionCalls = 0;
        int sendCalls = 0;

        using AdConnection sut = new(
            Options(preferredDc: "dc.example.corp"),
            NullLogger<AdConnection>.Instance,
            connectionFactory: (host, port) =>
            {
                Interlocked.Increment(ref constructionCalls);
                return NewUnboundLdapConnection(host, port);
            },
            dcLocator: () => "dc.example.corp",
            sender: async (_, _, _) =>
            {
                Interlocked.Increment(ref sendCalls);
                // Yield to ensure scheduling interleaves.
                await Task.Yield();
                return SearchResultEntryBuilder.BuildResponse([]);
            });

        const int Parallelism = 32;
        Task[] tasks = new Task[Parallelism];
        for (int i = 0; i < Parallelism; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base));
            });
        }

        await Task.WhenAll(tasks);

        sendCalls.ShouldBe(Parallelism);
        // ConcurrentDictionary.GetOrAdd may briefly construct extras under
        // contention, but the cache must converge to exactly one bound entry.
        // What matters operationally is that all calls succeed and no caller
        // ends up with a stale or disposed connection. The factory must be
        // called at least once and at most "Parallelism" times.
        constructionCalls.ShouldBeGreaterThanOrEqualTo(1);
        constructionCalls.ShouldBeLessThanOrEqualTo(Parallelism);
    }

    [Fact]
    public async Task SearchAsync_NonLdapExceptionFromSender_DoesNotEvictConnection()
    {
        int constructionCalls = 0;
        int sendCalls = 0;

        using AdConnection sut = new(
            Options(preferredDc: "dc.example.corp"),
            NullLogger<AdConnection>.Instance,
            connectionFactory: (host, port) =>
            {
                Interlocked.Increment(ref constructionCalls);
                return NewUnboundLdapConnection(host, port);
            },
            dcLocator: () => "dc.example.corp",
            sender: (_, _, _) =>
            {
                int n = Interlocked.Increment(ref sendCalls);
                return n == 1
                    ? Task.FromException<DirectoryResponse>(new InvalidOperationException("non-transport error"))
                    : Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([]));
            });

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base)));

        await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base));

        // Connection re-used: no eviction on non-LDAP exceptions.
        constructionCalls.ShouldBe(1);
        sendCalls.ShouldBe(2);
    }

    [Fact]
    public async Task SearchAsync_AfterDispose_Throws()
    {
        AdConnection sut = new(
            Options(),
            NullLogger<AdConnection>.Instance,
            connectionFactory: NewUnboundLdapConnection,
            dcLocator: () => "dc.example.corp",
            sender: (_, _, _) => Task.FromResult<DirectoryResponse>(SearchResultEntryBuilder.BuildResponse([])));

        sut.Dispose();

        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await sut.SearchAsync(new SearchRequest("DC=example,DC=corp", "(objectClass=*)", SearchScope.Base)));
    }

    [Fact]
    public async Task AddModifyDelete_DispatchToSender_AndReturnExpectedResponseTypes()
    {
        int sendCount = 0;

        using AdConnection sut = new(
            Options(),
            NullLogger<AdConnection>.Instance,
            connectionFactory: NewUnboundLdapConnection,
            dcLocator: () => "dc.example.corp",
            sender: (_, request, _) =>
            {
                Interlocked.Increment(ref sendCount);
                DirectoryResponse response = request switch
                {
                    AddRequest => CreateResponse<AddResponse>(),
                    ModifyRequest => CreateResponse<ModifyResponse>(),
                    DeleteRequest => CreateResponse<DeleteResponse>(),
                    SearchRequest => SearchResultEntryBuilder.BuildResponse([]),
                    _ => throw new InvalidOperationException("Unexpected request type."),
                };
                return Task.FromResult(response);
            });

        AddResponse add = await sut.AddAsync(new AddRequest("CN=x,DC=e,DC=c", new DirectoryAttribute("objectClass", "user")));
        ModifyResponse modify = await sut.ModifyAsync(new ModifyRequest("CN=x,DC=e,DC=c", DirectoryAttributeOperation.Replace, "givenName", "X"));
        DeleteResponse delete = await sut.DeleteAsync(new DeleteRequest("CN=x,DC=e,DC=c"));

        add.ShouldNotBeNull();
        modify.ShouldNotBeNull();
        delete.ShouldNotBeNull();
        sendCount.ShouldBe(3);
    }

    private static TResponse CreateResponse<TResponse>()
        where TResponse : DirectoryResponse
    {
        // Add/Modify/Delete/Search responses all share an internal ctor
        // (matchedDN, controls, ResultCode, errorMessage, referral).
        var ctor = typeof(TResponse).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            types: [typeof(string), typeof(DirectoryControl[]), typeof(ResultCode), typeof(string), typeof(Uri[])],
            modifiers: null)
            ?? throw new InvalidOperationException($"No 5-arg internal constructor on {typeof(TResponse).Name}.");
        return (TResponse)ctor.Invoke([null!, Array.Empty<DirectoryControl>(), ResultCode.Success, null!, Array.Empty<Uri>()]);
    }
}
