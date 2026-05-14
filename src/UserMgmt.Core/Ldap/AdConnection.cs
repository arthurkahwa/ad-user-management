using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.DirectoryServices.ActiveDirectory;
using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UserMgmt.Core.Ldap;

/// <summary>
/// Singleton <see cref="IAdConnection"/> implementation backed by a
/// per-domain-controller dictionary of bound <see cref="LdapConnection"/>
/// instances.
/// </summary>
/// <remarks>
/// Designed for the M1.2 read path and every subsequent AD-touching service.
/// Connections are lazy-bound on first use per DC; on any
/// <see cref="LdapException"/> bubbled from a request the cached entry for
/// that DC is evicted and the next call reconstructs it. DC selection uses
/// <c>System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain()</c>
/// / <c>FindDomainController()</c>, overridable via
/// <see cref="AdOptions.PreferredDc"/>.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "LdapConnection instances are owned by the long-lived singleton dictionary and disposed via Dispose().")]
public sealed class AdConnection : IAdConnection, IDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogConnectionEvicted =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1001, nameof(LogConnectionEvicted)),
            "LDAP request failed for DC '{Host}'; evicting cached connection so the next call reconnects.");

    private static readonly Action<ILogger, string, Exception?> LogConnectionDisposeFailure =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(1002, nameof(LogConnectionDisposeFailure)),
            "Suppressed exception while disposing LDAP connection for '{Host}'.");

    private readonly ConcurrentDictionary<string, LdapConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly AdOptions _options;
    private readonly ILogger<AdConnection> _logger;
    private readonly Func<string, int, LdapConnection> _connectionFactory;
    private readonly Func<string> _dcLocator;
    private readonly Func<LdapConnection, DirectoryRequest, CancellationToken, Task<DirectoryResponse>> _sender;
    private readonly object _hostnameLock = new();
    private string? _resolvedDcHostname;
    private bool _disposed;

    /// <summary>Production constructor — DI binds this overload.</summary>
    [SuppressMessage(
        "Interoperability",
        "CA1416:Validate platform compatibility",
        Justification = "DefaultDcLocator only runs against a real AD domain, which means Windows Server. Test-only paths inject a stub locator through the internal constructor.")]
    public AdConnection(IOptions<AdOptions> options, ILogger<AdConnection> logger)
        : this(
            options,
            logger,
            connectionFactory: DefaultConnectionFactory,
            dcLocator: DefaultDcLocator,
            sender: DefaultSendAsync)
    {
    }

    /// <summary>Test-seam constructor allowing factory / locator / send overrides.</summary>
    /// <remarks>
    /// Internal so unit tests in <c>UserMgmt.Core.Tests</c> (via
    /// <c>InternalsVisibleTo</c>) can inject deterministic stand-ins for the
    /// transport and locator without spinning up a real DC.
    /// </remarks>
    internal AdConnection(
        IOptions<AdOptions> options,
        ILogger<AdConnection> logger,
        Func<string, int, LdapConnection> connectionFactory,
        Func<string> dcLocator,
        Func<LdapConnection, DirectoryRequest, CancellationToken, Task<DirectoryResponse>> sender)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(dcLocator);
        ArgumentNullException.ThrowIfNull(sender);

        _options = options.Value;
        _logger = logger;
        _connectionFactory = connectionFactory;
        _dcLocator = dcLocator;
        _sender = sender;
    }

    /// <inheritdoc />
    public int Port => _options.Port;

    /// <inheritdoc />
    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SearchResponse>(request, cancellationToken);

    /// <inheritdoc />
    public Task<AddResponse> AddAsync(AddRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AddResponse>(request, cancellationToken);

    /// <inheritdoc />
    public Task<ModifyResponse> ModifyAsync(ModifyRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ModifyResponse>(request, cancellationToken);

    /// <inheritdoc />
    public Task<DeleteResponse> DeleteAsync(DeleteRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<DeleteResponse>(request, cancellationToken);

    private async Task<TResponse> SendAsync<TResponse>(DirectoryRequest request, CancellationToken cancellationToken)
        where TResponse : DirectoryResponse
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string host = ResolveDcHostname();
        LdapConnection connection = GetOrCreate(host);

        try
        {
            DirectoryResponse response = await _sender(connection, request, cancellationToken).ConfigureAwait(false);
            return (TResponse)response;
        }
        catch (LdapException ex)
        {
            LogConnectionEvicted(_logger, host, ex);
            EvictAndDispose(host, connection);
            throw;
        }
    }

    private static Task<DirectoryResponse> DefaultSendAsync(
        LdapConnection connection,
        DirectoryRequest request,
        CancellationToken cancellationToken)
    {
        // TaskFactory.FromAsync wraps the Begin/End pair into a Task without
        // spawning a thread-pool work item — no Task.Run shims.
        Task<DirectoryResponse> task = Task.Factory.FromAsync(
            (AsyncCallback callback, object? state) => connection.BeginSendRequest(
                request,
                PartialResultProcessing.NoPartialResultSupport,
                callback,
                state),
            connection.EndSendRequest,
            state: null);

        if (!cancellationToken.CanBeCanceled)
        {
            return task;
        }

        // Bridge cooperative cancellation; the underlying LDAP transport has
        // no first-class cancellation API so we surface the cancellation
        // through the awaited Task rather than aborting the request itself.
        return task.WaitAsync(cancellationToken);
    }

    private string ResolveDcHostname()
    {
        if (!string.IsNullOrWhiteSpace(_options.PreferredDc))
        {
            return _options.PreferredDc;
        }

        if (_resolvedDcHostname is not null)
        {
            return _resolvedDcHostname;
        }

        lock (_hostnameLock)
        {
            _resolvedDcHostname ??= _dcLocator();
            return _resolvedDcHostname;
        }
    }

    private LdapConnection GetOrCreate(string host) =>
        _connections.GetOrAdd(host, h => _connectionFactory(h, _options.Port));

    private void EvictAndDispose(string host, LdapConnection connection)
    {
        // Only dispose if we successfully removed the exact instance we
        // observed — a concurrent caller may have already evicted and
        // replaced it, and we must not dispose a fresh connection that
        // another caller is using.
        if (_connections.TryRemove(new KeyValuePair<string, LdapConnection>(host, connection)))
        {
            try
            {
                connection.Dispose();
            }
#pragma warning disable CA1031 // Catch all on eviction-path disposal — never let a bad Dispose mask the original LdapException.
            catch (Exception ex)
            {
                LogConnectionDisposeFailure(_logger, host, ex);
            }
#pragma warning restore CA1031
        }
    }

    private static LdapConnection DefaultConnectionFactory(string host, int port)
    {
        LdapDirectoryIdentifier identifier = new(host, port);
        LdapConnection connection = new(identifier)
        {
            AuthType = AuthType.Negotiate,
        };
        connection.SessionOptions.ProtocolVersion = 3;
        if (port == 636)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }

        connection.Bind();
        return connection;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string DefaultDcLocator()
    {
        // System.DirectoryServices.ActiveDirectory is Windows-only; in
        // production the host is Windows Server. Tests inject a stub locator
        // through the internal constructor, so this method is unreachable on
        // non-Windows test runners.
        using System.DirectoryServices.ActiveDirectory.Domain domain =
            System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain();
        using DomainController controller = domain.FindDomainController();
        return controller.Name;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (KeyValuePair<string, LdapConnection> kvp in _connections)
        {
            try
            {
                kvp.Value.Dispose();
            }
#pragma warning disable CA1031 // Drain disposal — one bad connection must not block the rest.
            catch (Exception ex)
            {
                LogConnectionDisposeFailure(_logger, kvp.Key, ex);
            }
#pragma warning restore CA1031
        }

        _connections.Clear();
    }
}
