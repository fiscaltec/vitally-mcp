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

    /// <summary>
    /// The exception attached to each entry, index-aligned with <see cref="Entries"/>. Separate because
    /// the message formatter ignores the exception, so a record that attaches one — whose
    /// <c>ToString()</c> the console and the OTel exporter both render — looks identical in
    /// <see cref="Entries"/> to one that does not. The failure logs (#94) depend on telling them apart.
    /// </summary>
    public List<Exception?> Exceptions { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Entries.Add((logLevel, formatter(state, exception)));
        Exceptions.Add(exception);
    }

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
/// <remarks>
/// <paramref name="matchPrefix"/> widens the match to every category starting with the name — for a
/// third-party component such as the MCP SDK, whose exact category is an implementation detail.
/// </remarks>
public sealed class CapturingLoggerProvider(string categoryName, bool matchPrefix = false) : ILoggerProvider
{
    private readonly CapturingLogger<object> _sink = new();

    /// <summary>Entries logged under <c>categoryName</c>, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _sink.Entries;

    /// <summary>The exception attached to each entry, index-aligned with <see cref="Entries"/>.</summary>
    public IReadOnlyList<Exception?> Exceptions => _sink.Exceptions;

    public ILogger CreateLogger(string category) =>
        category == categoryName || (matchPrefix && category.StartsWith(categoryName, StringComparison.Ordinal))
            ? _sink
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }
}

/// <summary>
/// Captures the structured <b>state</b> of each record, not just its rendered message.
/// </summary>
/// <remarks>
/// Needed because the Azure Monitor exporter decides an audit record's destination table by looking
/// for one exact attribute key in the log state. A record missing it, or carrying a misspelling, is
/// written to <c>AppTraces</c> silently — no error, no warning — so the only way to catch that before
/// it reaches Azure is to assert on the state the logger was handed.
/// </remarks>
public sealed class StateCapturingLogger<T> : ILogger<T>
{
    public List<IReadOnlyList<KeyValuePair<string, object?>>> States { get; } = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (state is IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            States.Add(fields);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
