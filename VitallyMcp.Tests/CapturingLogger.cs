using Microsoft.Extensions.Logging;

namespace VitallyMcp.Tests;

/// <summary>
/// Collects log entries as <c>(Level, Message)</c> pairs for assertion. Used directly by
/// <see cref="AuditLoggerTests"/> against a hand-built <see cref="AuditLogger"/>, and via
/// <see cref="CapturingLoggerProvider"/> to capture the same records from a real host in the
/// integration tests — one capture shape for both.
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that captures entries from a single logger category, so an
/// integration test can assert on what a component inside a composed host actually logged. Scoped to
/// one category on purpose — the audit assertions must count only <see cref="AuditLogger"/> records,
/// not every framework message the host happens to emit.
/// </summary>
public sealed class CapturingLoggerProvider(string categoryName) : ILoggerProvider
{
    private readonly CapturingLogger<object> _sink = new();

    /// <summary>Entries logged under <c>categoryName</c>, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _sink.Entries;

    public ILogger CreateLogger(string category) =>
        category == categoryName ? _sink : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }
}
