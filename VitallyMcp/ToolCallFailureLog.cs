using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VitallyMcp;

/// <summary>
/// The server-side record of a failed <c>tools/call</c> (#94). Until this existed the error-surfacing
/// filter in <c>Program.cs</c> handed the failure to the client and logged nothing, so a Vitally
/// outage, a validation failure or a Key Vault failure left no trace on the server at all.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Only the exception TYPE is logged, never its message.</b> The message is exactly what the
/// client is shown, and that is the problem: <see cref="VitallyService"/> puts a truncated copy of the
/// upstream response body into an <see cref="HttpRequestException"/>, and the date validation echoes
/// the caller's own input into an <see cref="ArgumentException"/>. Either can carry customer data, and
/// this category is not suppressed from the console the way <c>VitallyMcp.AuditLogger</c> is. The
/// correlation id is what recovers the rest: it joins this line to the tool-call audit record, which
/// carries the arguments under the audit table's access control.
/// </para>
/// <para>
/// Its own category rather than <see cref="AuditLogger"/>'s, because an operational failure belongs
/// on the console and in <c>AppTraces</c> where an operator looks, not only in <c>AppEvents</c>.
/// </para>
/// </remarks>
public static class ToolCallFailureLog
{
    public const string Category = "VitallyMcp.ToolCallFailures";

    /// <summary>
    /// The filter's entry point: resolves the logger and the correlation id from the request's own
    /// services. The correlation id must come from the <b>scoped</b> <see cref="ToolCallAuditContext"/>
    /// — the same instance the tool's <see cref="VitallyService"/> wrote into — or this line cannot be
    /// joined to the audit records for the same call.
    /// </summary>
    public static void Write(IServiceProvider? services, string? toolName, Exception ex,
        CancellationToken cancellationToken)
    {
        var logger = services?.GetService<ILoggerFactory>()?.CreateLogger(Category);
        if (logger is null)
        {
            return;
        }

        Write(logger, toolName ?? "unknown", ex,
            services?.GetService<ToolCallAuditContext>()?.CorrelationId, cancellationToken);
    }

    public static void Write(ILogger logger, string toolName, Exception ex, string? correlationId,
        CancellationToken cancellationToken)
    {
        // Never the reason a call fails: this runs on the way to returning the tool's own result, and
        // a telemetry sink refusing writes is the sort of thing that happens during an incident. The
        // same rule as the audit filter, for the same reason.
        try
        {
            WriteCore(logger, toolName, ex, correlationId ?? "none", cancellationToken);
        }
        catch (Exception logFailure) when (logFailure is not OperationCanceledException)
        {
            // Deliberately ignored.
        }
    }

    private static void WriteCore(ILogger logger, string toolName, Exception ex, string correlationId,
        CancellationToken cancellationToken)
    {
        var type = ex.GetType().Name;
        switch (ex)
        {
            // Gated on the CALLER's token rather than the exception type. An HttpClient timeout is a
            // TaskCanceledException too, and that is a slow upstream — precisely the failure worth
            // seeing — so filtering the type out would hide it. Same reasoning as the OIDC fallback.
            case OperationCanceledException when cancellationToken.IsCancellationRequested:
                return;

            // Already recorded by AuditLogger.LogDenied, with the caller and the resource path. A
            // denial is the system working, and logging it at Error would make it look like a fault.
            case UnauthorizedAccessException:
                return;

            // The caller's input was rejected before anything reached Vitally. Worth seeing — a tool
            // description that keeps producing bad arguments is a content defect — but not a fault.
            case ArgumentException:
                logger.LogWarning(
                    "Tool call {ToolName} rejected its arguments: {ExceptionType} correlation={CorrelationId}",
                    toolName, type, correlationId);
                return;

            // No status code means the request never got an answer: DNS, connection refused, TLS. The
            // non-2xx record in VitallyService.SendAsync cannot fire for that, so this is the only trace.
            case HttpRequestException http:
                logger.LogError(
                    "Tool call {ToolName} failed upstream: {ExceptionType} status={StatusCode} correlation={CorrelationId}",
                    toolName, type, http.StatusCode is { } status ? (object)(int)status : "none", correlationId);
                return;

            default:
                logger.LogError(
                    "Tool call {ToolName} failed unexpectedly: {ExceptionType} correlation={CorrelationId}",
                    toolName, type, correlationId);
                return;
        }
    }
}
