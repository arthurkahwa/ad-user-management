using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace UserMgmt.Core.Tests.Fixtures;

/// <summary>
/// In-memory <see cref="ILogger{T}"/> implementation that captures every log
/// message into a thread-safe list. Used by tests that need to assert what
/// the SUT did (or, more importantly, did NOT) write to the log.
/// </summary>
/// <remarks>
/// The default formatter is applied so the captured strings match what an
/// ordinary console logger would emit. <see cref="Captured"/> snapshots a
/// stable copy so assertions don't race the writer.
/// </remarks>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _messages = new();

    /// <summary>All captured messages in order they were emitted.</summary>
    public IReadOnlyList<string> Captured => _messages.ToArray();

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        _messages.Enqueue(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
