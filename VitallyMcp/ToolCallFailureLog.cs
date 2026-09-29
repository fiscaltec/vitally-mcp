using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace VitallyMcp;

/// <summary>
/// The server-side record of a failed <c>tools/call</c> (#94). Until this existed the error-surfacing
/// filter in <c>Program.cs</c> handed a surfaceable failure — a Vitally error, a rejected argument —
/// to the client and logged nothing, so those left no server-side trace at all. (A non-surfaceable
/// exception, such as a Key Vault failure, was already logged by the MCP SDK itself; this adds the
/// correlation id that joins it to the audit record.)
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Only the exception TYPE is logged, never its message, and the exception is never
/// attached.</b> The message is exactly what the client is shown, and that is the problem:
/// <see cref="VitallyService"/> puts a truncated copy of the upstream response body into an
/// <see cref="HttpRequestException"/>, and the date validation echoes the caller's own input into an
/// <see cref="ArgumentException"/>. Either can carry customer data, and this category is not
/// suppressed from the console the way <c>VitallyMcp.AuditLogger</c> is. Attaching the exception
/// would leak the same text by another route, since the console formatter and the OTel exporter both
/// render it. The correlation id is what recovers the rest: it joins this line to the tool-call audit
/// record, which carries the arguments under the audit table's access control.
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
    /// The filter's entry point: resolves the logger, the registered tool names and the correlation id
    /// from the request's own services. The correlation id must come from the <b>scoped</b>
    /// <see cref="ToolCallAuditContext"/> — the same instance the tool's <see cref="VitallyService"/>
    /// wrote into — or this line cannot be joined to the audit records for the same call.
    /// </summary>
    /// <remarks>
    /// The name is checked against <see cref="KnownToolNames"/> because a <c>tools/call</c> naming a
    /// tool that does not exist still reaches this filter, so the name is caller-controlled text — and
    /// it would be going to the console. Fail closed, as the audit breadcrumb does: with no registered
    /// set to check against, every name reads <c>unrecognised</c>.
    /// </remarks>
    public static void Write(IServiceProvider? services, string? toolName, Exception ex,
        CancellationToken cancellationToken)
    {
        // Resolution sits inside the guard too: a throw here (a disposed request scope, say) would
        // escape the filter's catch and replace the tool's own error result with the SDK's generic one.
        try
        {
            var logger = services?.GetService<ILoggerFactory>()?.CreateLogger(Category);
            if (logger is null)
            {
                return;
            }

            var knownTools = services?.GetService<KnownToolNames>();
            var safeName = knownTools?.IsRegistered(toolName) == true ? toolName! : "unrecognised";
            WriteCore(logger, safeName, ex,
                services?.GetService<ToolCallAuditContext>()?.CorrelationId ?? "none", cancellationToken);
        }
        catch (Exception logFailure) when (logFailure is not OperationCanceledException)
        {
            // Deliberately ignored. See the overload below.
        }
    }

    /// <summary>Writes the record for an already-vetted tool name.</summary>
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

    /// <summary>
    /// The level an upstream status earns. <b>Error</b> for what is ours or Vitally's to fix — a 5xx,
    /// a 401/403 (the shared key is bad or revoked), a 429 (the request budget) or no response at all.
    /// <b>Warning</b> for any other 4xx, which is the caller's input: a wrong id from the model is
    /// routine, and logging it at Error would bury a real outage once #159 alerts on that level.
    /// </summary>
    /// <remarks>
    /// 407 and 408 are infrastructure conditions despite sitting in the 4xx range, so they are Error
    /// too. ⚠️ Known blind spot: a 4xx on a request the caller did not shape — Vitally renaming an
    /// endpoint, or rejecting a parameter this server adds itself — is logged at Warning, so an
    /// Error-only alert would not see it. The rate of Warnings is the signal there.
    /// </remarks>
    public static LogLevel LevelFor(HttpStatusCode? status) => status switch
    {
        null => LogLevel.Error,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ProxyAuthenticationRequired
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => LogLevel.Error,
        >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => LogLevel.Warning,
        _ => LogLevel.Error,
    };

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

            // Anything else cancelled is a timeout — HttpClient's (100 s by default) or Azure.Core's.
            // Named as such rather than left to "failed unexpectedly", because it is an expected
            // failure mode with its own cause; the rate limiter's waits can run into it.
            case OperationCanceledException:
                logger.LogError(
                    "Tool call {ToolName} timed out: {ExceptionType} correlation={CorrelationId}",
                    toolName, type, correlationId);
                return;

            // Recorded by AuditLogger.LogDenied, with the caller and the resource path — when
            // Audit:Enabled is on, which it is on every deployed target. A denial is the system
            // working, and logging it at Error would make it look like a fault.
            case UnauthorizedAccessException:
                return;

            // The caller's input was rejected before anything reached Vitally. Worth seeing — a tool
            // description that keeps producing bad arguments is a content defect — but not a fault.
            // The PLAIN type only: that is what this code throws for bad input and what the SDK throws
            // for a missing parameter, whereas an ArgumentOutOfRange/ArgumentNull is almost always a
            // bug of ours and falls through to Error below.
            case ArgumentException when ex.GetType() == typeof(ArgumentException):
                logger.LogWarning(
                    "Tool call {ToolName} rejected its arguments: {ExceptionType} correlation={CorrelationId}",
                    toolName, type, correlationId);
                return;

            // A protocol-level refusal — an unknown tool, a malformed request. The client's error.
            case McpException:
                logger.LogWarning(
                    "Tool call {ToolName} was refused by the MCP layer: {ExceptionType} correlation={CorrelationId}",
                    toolName, type, correlationId);
                return;

            // No status code means the request never got an answer. HttpRequestError says which of
            // DNS, a refused connection or TLS it was, and is an enum, so it carries no customer data.
            // The non-2xx record in VitallyService.SendAsync cannot fire without a response, so for
            // that case this is the only trace.
            case HttpRequestException http:
                logger.Log(LevelFor(http.StatusCode),
                    "Tool call {ToolName} failed upstream: {ExceptionType} status={StatusCode} error={HttpRequestError} correlation={CorrelationId}",
                    toolName, type, http.StatusCode is { } status ? (object)(int)status : "none",
                    // Unknown whenever a response arrived, which would read as "an unknown error".
                    http.StatusCode is null ? (object)http.HttpRequestError : "none", correlationId);
                return;

            default:
                logger.LogError(
                    "Tool call {ToolName} failed unexpectedly: {ExceptionType} correlation={CorrelationId}",
                    toolName, type, correlationId);
                return;
        }
    }
}
