using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Emits per-user audit records for Vitally actions, using structured logging so the named properties
/// are shaped as queryable dimensions. Every record is keyed on the caller's identity as
/// <see cref="ResolveUserId(ClaimsPrincipal?)"/> resolves it — Entra <b>object id</b> first (see
/// <see cref="CallerIdentity"/>), then the raw <c>sub</c>, then <c>NameIdentifier</c>, then
/// <c>unknown</c>; an unauthenticated caller is <c>anonymous</c>.
/// <para>
/// <b>Four record shapes, not one</b>, because they are emitted at different points:
/// <list type="bullet">
///   <item><see cref="LogToolCall"/> — the <b>primary</b> record, one per <i>executed</i> tool call:
///     identity, tool, arguments, the record ids touched, outcome, duration, correlation id, and the
///     permission tier resolved at the time. This is the one that satisfies the acceptance criterion.
///     <para>
///     ⚠️ <b>"Executed" is the exact word.</b> It is emitted from a call-tool filter, and the SDK's
///     <c>[Authorize]</c> checkpoint rejects an out-of-tier call <i>outside</i> that pipeline — so a
///     <b>tier-denied call produces no record of this shape</b>, only <see cref="LogToolCallDenied"/>.
///     The trail is not blind there (that record names the caller, the tool and the permission
///     required) but it carries no arguments, no correlation id and no touched records, so a denial
///     cannot say what the caller was reaching for. That is a deliberate boundary of #147 rather than
///     an oversight, and it is the same open question noted on
///     <see cref="LogToolCallDenied"/>.</para></item>
///   <item><see cref="LogAction"/> — from <see cref="VitallyService.SendAsync"/> after each upstream
///     response: identity, verb, resource path (query string stripped), status, and the correlation
///     id of the tool call that caused it — without which the join the tool-call record promises
///     does not exist. <b>Corroboration</b>
///     now rather than the mechanism: it shows what the server actually did, but names a customer
///     only where the path carries an id — which excludes unscoped list and search.</item>
///   <item><see cref="LogDenied"/> — from the same choke point on an RBAC refusal: identity, verb,
///     path. <b>No status</b>, because the call never happened.</item>
///   <item><see cref="LogToolCallDenied"/> — from the SDK <c>[Authorize]</c> checkpoint, which rejects
///     <i>before</i> <c>SendAsync</c> runs: identity, tool name, required permission. <b>No verb or
///     path</b>, because no upstream call was attempted. This exists precisely because
///     <see cref="LogDenied"/> would never see a tier mismatch.</item>
/// </list>
/// </para>
/// <para>
/// ⚠️ <b>Two things this comment used to claim are no longer true.</b> First, the records are
/// <i>not</i> queryable: nothing this server logs has ever reached Application Insights or Log
/// Analytics (verified 2026-09-17 — the workspace refuses the Container Apps shared-key shipper
/// because local authentication is disabled on it). See issue #142. Second, the blanket "keep
/// personal data out of telemetry" policy was <b>withdrawn on 2026-09-17</b>: the agreed design
/// records tool arguments, including search terms that may carry names or email addresses, because
/// without them the trail cannot say <i>which customer</i> was accessed. Upstream response bodies
/// remain excluded — they can carry meeting transcripts and arbitrary traits.
/// </para>
/// <para>
/// <b>The tool-call record is implemented</b> (#147) — arguments, returned record ids, counts,
/// correlation id, and the effective permission tier. What is <i>not</i> yet done is routing: these
/// records still go through <see cref="ILogger"/> to stdout, and stdout is not exported, so nothing
/// here is queryable yet. Until <see cref="LogToolCall"/> writes via <c>TrackEvent</c> and the console
/// provider is suppressed for this category, #142's console-log export stays gated — exporting it
/// sooner would put customer identifiers into the table with the shortest retention and the broadest
/// access. Design: <c>docs/superpowers/specs/2026-09-17-logging-observability-design.md</c>.
/// </para>
/// </summary>
/// <remarks>
/// The object id rather than <c>sub</c>, which is what this used to record. An Entra v2 <c>sub</c> is
/// a <i>pairwise</i> identifier — unique per (user, application) and not resolvable to a person by
/// any Entra lookup — so an audit trail keyed on it is consistent but unattributable, which defeats
/// the point. Found by decoding a real staging token during the #108 cutover validation, before the
/// production flip could start writing such records.
/// </remarks>
public class AuditLogger
{
    private readonly AuditOptions _options;
    private readonly ILogger<AuditLogger> _logger;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly ILogger? _fallback;

    /// <summary>
    /// Category for the breadcrumb that stays on the console when the full record does not.
    /// </summary>
    /// <remarks>
    /// Its own category so the two can be routed in opposite directions: <c>Program.cs</c> suppresses
    /// the full record from the console provider and this one from the OpenTelemetry provider, so
    /// each record goes to exactly one destination rather than both.
    /// </remarks>
    private const string FallbackCategory = "VitallyMcp.AuditLogger.Fallback";

    public AuditLogger(
        IOptions<AuditOptions> options,
        ILogger<AuditLogger> logger,
        IHttpContextAccessor? httpContextAccessor = null,
        ILoggerFactory? loggerFactory = null)
    {
        _options = options.Value;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _fallback = loggerFactory?.CreateLogger(FallbackCategory);
    }

    /// <summary>Records a completed action (after the upstream response, success or failure).</summary>
    public void LogAction(HttpMethod method, string url, int statusCode, string? correlationId = null)
    {
        if (!_options.Enabled)
        {
            return;
        }
        if (method == HttpMethod.Get && !_options.IncludeReads)
        {
            return;
        }

        Emit(() => _logger.LogInformation(
            "Vitally audit: {AuditUserId} {HttpMethod} {VitallyResource} -> {StatusCode} correlation={AuditCorrelationId}",
            ResolveUserId(), method.Method, ResourcePath(url), statusCode, correlationId ?? "none"));
    }

    /// <summary>
    /// Records one tool call: who, which tool, with what arguments, and which records it touched.
    /// </summary>
    /// <remarks>
    /// This is the record that satisfies the acceptance criterion. <see cref="LogAction"/> remains as
    /// corroboration — it shows what the server actually did — but it names a customer only where the
    /// upstream path carries an id, which excludes unscoped list and search, and those are most reads.
    /// </remarks>
    public void LogToolCall(ToolCallAudit call)
    {
        if (!_options.Enabled)
        {
            return;
        }

        Emit(() => _logger.LogInformation(
            "Vitally audit: {AuditUserId} called {McpToolName} args={McpToolArguments} "
            + "records={AuditRecordIds} fetched={AuditRecordsFetched} ids={AuditIdsRecorded} "
            + "truncated={AuditPagerTruncated} argsTruncated={AuditArgumentsTruncated} "
            + "unreadable={AuditCallsWithoutIds} "
            + "outcome={AuditOutcome} durationMs={AuditDurationMs} correlation={AuditCorrelationId} "
            + "tier={AuditPermissionTier} tierStale={AuditTierServedStale} client={McpClientName} "
            + "event={microsoft.custom_event.name}",
            ResolveUserId(),
            Flatten(call.ToolName, MaxToolNameChars),
            call.Arguments.Rendered,
            SanitiseIds(call.Records.Ids),
            call.Records.RecordsFetched,
            call.Records.IdsRecorded,
            call.Records.Truncated,
            // Separate from the pager flag above and must stay separate: one says the matching total
            // is unknown, the other says the arguments recorded are not the ones the caller sent.
            call.Arguments.Truncated,
            call.Records.CallsWithoutIds,
            call.Outcome,
            (long)call.Duration.TotalMilliseconds,
            call.CorrelationId,
            call.PermissionTier,
            call.TierServedStale?.ToString() ?? "unknown",
            SanitiseClientName(call.McpClient),
            ToolCallEventName));

        // A breadcrumb the console keeps, because OpenTelemetry export is ASYNCHRONOUS: an ingestion
        // outage cannot throw back into this call, so a lost export would take the record with it —
        // from AppEvents and from stdout both — which is what #147 forbids ("records degrade rather
        // than disappear silently"). Emitted unconditionally rather than on a failure we cannot
        // observe.
        //
        // It carries who, what and the correlation id, and deliberately NO arguments and NO record
        // ids: the console table is the one the data map declares customer-data-free, and #142's
        // export is gated on that staying true. Enough to prove a call happened and to join it to the
        // upstream records, without putting a second copy of the customer data somewhere broader.
        if (_fallback is not null)
        {
            Emit(() => _fallback.LogInformation(
                "Vitally audit breadcrumb: {AuditUserId} called {McpToolName} outcome={AuditOutcome} "
                + "correlation={AuditCorrelationId}",
                ResolveUserId(),
                Flatten(call.ToolName, MaxToolNameChars),
                call.Outcome,
                call.CorrelationId));
        }
    }

    /// <summary>Records an action the caller was not permitted to perform (RBAC denial).</summary>
    public void LogDenied(HttpMethod method, string url, string? correlationId = null)
    {
        if (!_options.Enabled)
        {
            return;
        }

        Emit(() => _logger.LogWarning(
            "Vitally audit: {AuditUserId} DENIED {HttpMethod} {VitallyResource} correlation={AuditCorrelationId}",
            ResolveUserId(), method.Method, ResourcePath(url), correlationId ?? "none"));
    }

    /// <summary>
    /// Records a tool call refused on the caller's permission tier. Needed because the MCP SDK's
    /// authorisation checkpoint rejects an out-of-tier <c>tools/call</c> <b>before</b> the handler
    /// runs, so <see cref="VitallyService"/> — and therefore <see cref="LogDenied"/> — is never
    /// reached. Without this, tier-mismatch denials (the event class most worth auditing) would go
    /// unrecorded.
    ///
    /// <para>
    /// Called from <see cref="VitallyPermissionHandler"/>, which passes the policy's own principal
    /// rather than relying on the ambient HTTP context. Records the caller's object id, the tool name
    /// and the permission required — never the caller's email.
    /// </para>
    /// <para>
    /// ⚠️ It does <b>not</b> record the call arguments, and the reason is no longer the one this
    /// comment used to give. "They can carry customer PII" was the pre-2026-09-17 rule, withdrawn —
    /// <see cref="LogToolCall"/> records arguments in full. The arguments are simply out of scope for
    /// #147, which defined the tool-call record. Whether a <i>denied</i> call should record what the
    /// caller tried to reach is a real question — it is the difference between "someone was refused"
    /// and "someone was refused while reaching for this customer" — and it is open, not settled.
    /// </para>
    /// </summary>
    public void LogToolCallDenied(ClaimsPrincipal? user, string? toolName, string requiredPermission)
    {
        if (!_options.Enabled)
        {
            return;
        }

        Emit(() => _logger.LogWarning(
            "Vitally audit: {AuditUserId} DENIED tools/call {McpToolName} (requires {RequiredPermission})",
            ResolveUserId(user), Flatten(toolName ?? "unknown", MaxToolNameChars), requiredPermission));
    }

    /// <summary>
    /// Longest client name a record will carry. Short because it names a product, not a value.
    /// </summary>
    private const int MaxClientNameChars = 64;

    private const string TruncationMarker = "...";

    /// <summary>
    /// Makes an untrusted client name safe to put in a line-oriented log.
    /// </summary>
    /// <remarks>
    /// <b>This value is supplied by the caller</b>, in the per-request
    /// <c>_meta/io.modelcontextprotocol/clientInfo</c>, so it is attacker-controlled in a way the
    /// rest of the record is not. Two consequences, both handled here:
    /// <list type="bullet">
    ///   <item>A newline would let a client emit what looks like a <i>second</i> audit record —
    ///     forging an action against another user's object id. Control characters are replaced, not
    ///     stripped, so the attempt stays visible rather than being quietly cleaned away.</item>
    ///   <item>An unbounded name inflates every record the client makes, on a path that is about to
    ///     become billed telemetry. Tool arguments are explicitly bounded; this must be too.</item>
    /// </list>
    /// The same reasoning already governs <c>Program.cs</c>'s <c>OnAuthenticationFailed</c>, which
    /// logs the exception <i>type</i> and never <c>Exception.Message</c>, because IdentityModel
    /// builds that text from the token's own claims.
    /// </remarks>
    /// <summary>
    /// Whether a character can start a new line in a line-oriented log.
    /// </summary>
    /// <remarks>
    /// <c>char.IsControl</c> alone is not enough, and the gap is not obvious: <b>U+2028 LINE
    /// SEPARATOR and U+2029 PARAGRAPH SEPARATOR are categories Zl and Zp, not Cc</b>, so
    /// <c>IsControl</c> returns <c>false</c> for them while plenty of log readers still break lines
    /// on them. Sanitising only the C0 controls closed half the injection this method exists to stop.
    /// Checking the unicode category covers both without magic numbers.
    /// </remarks>
    /// <summary>
    /// Replaces anything that could break a line, and bounds the length.
    /// </summary>
    /// <remarks>
    /// Allocates the CAPPED length, not the caller's. Sanitising the whole string and trimming
    /// afterwards meant a 10 MB value forced a 10 MB allocation and scan per call for output that was
    /// always going to be short — a bound on the record that was not a bound on the work.
    /// </remarks>
    private static string Flatten(string value, int max)
    {
        var oversized = value.Length > max;
        var kept = oversized ? max : value.Length;
        var flattened = string.Create(kept, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = IsLineBreaking(source[i]) ? '_' : source[i];
            }
        });

        return oversized ? flattened + TruncationMarker : flattened;
    }

    private static bool IsLineBreaking(char c) =>
        char.IsControl(c)
        || char.GetUnicodeCategory(c)
            is System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Groups the tool-call records in the destination table.
    /// </summary>
    /// <remarks>
    /// <b>The placeholder carrying this is what decides which table the record lands in.</b> The
    /// Azure Monitor exporter looks for the attribute key <c>microsoft.custom_event.name</c> —
    /// exactly, case-sensitively — in the log state, and writes an <c>AppEvents</c> row when it finds
    /// one. Without it, or with it misspelled, the record becomes an <c>AppTraces</c> row instead:
    /// <b>silently</b>, with no error and no warning, sharing a table with ordinary diagnostics and
    /// losing the per-table retention and access control this routing exists to obtain.
    /// <para>
    /// So the odd-looking placeholder name in the template below is load-bearing rather than
    /// decorative, and <c>AuditLoggerTests</c> asserts its exact spelling. Note also that a record
    /// carrying an <b>exception</b> is emitted as <c>AppExceptions</c> regardless of this attribute —
    /// which is why <see cref="Emit"/> never passes one.
    /// </para>
    /// </remarks>
    private const string ToolCallEventName = "VitallyToolCall";

    /// <summary>
    /// Longest a tool name may be in the message.
    /// </summary>
    /// <remarks>
    /// The tool name arrives in the caller's own <c>tools/call</c> params, so it is as
    /// attacker-controlled as the client name and the record ids. A call naming a tool that does not
    /// exist still reaches the audit filter, so an unresolvable name carrying a newline would forge a
    /// record — the fourth field of this shape in this change, and one the "everything
    /// caller-controlled reaching this line is flattened" rule should already have covered.
    /// </remarks>
    private const int MaxToolNameChars = 128;

    /// <summary>Longest a single record id may be in the message.</summary>
    private const int MaxIdChars = 128;

    /// <summary>
    /// Renders the touched record ids safely for a line-oriented log.
    /// </summary>
    /// <remarks>
    /// The ids are <b>not</b> purely server-side data. <c>AuditRecordIds.FromMutationUrl</c> decodes a
    /// path segment the caller supplied, so a tool invoked with an id of <c>acc%0AVitally audit: …</c>
    /// yields a real newline — the same forgery the client name and the argument values are already
    /// sanitised against, reached through the one field that was not. Each id is flattened and
    /// length-bounded before it reaches the message.
    /// </remarks>
    private static string SanitiseIds(IReadOnlyList<string> ids) =>
        string.Join(",", ids.Select(id => Flatten(id, MaxIdChars)));

    private static string SanitiseClientName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "unknown" : Flatten(name, MaxClientNameChars);

    /// <summary>
    /// Writes one record, absorbing any failure.
    /// </summary>
    /// <remarks>
    /// <b>An audit write must never be the reason a tool call fails.</b> These are invoked from
    /// <see cref="VitallyService.SendAsync"/> and from a call-tool filter's <c>finally</c>, so an
    /// exception escaping here does not merely lose a record — it replaces the caller's result with
    /// <i>"An error occurred invoking 'X'"</i> for a call that in fact succeeded. A telemetry sink
    /// refusing writes is exactly the sort of thing that happens during the incident the trail is
    /// wanted for, and losing the user's call as well as the record is the worse half of that.
    /// <para>
    /// Swallowed rather than re-logged, because the logger <i>is</i> the sink that just failed.
    /// Once the record routes through <c>TrackEvent</c>, the fallback the design calls for — degrade
    /// to <see cref="ILogger"/> rather than disappear — becomes possible and belongs here.
    /// </para>
    /// </remarks>
    private static void Emit(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Deliberately ignored, and deliberately every exception: there is no failure from a
            // logging sink that is worth failing a customer's tool call over.
        }
    }

    // Resolve the stable, attributable actor identity: the caller's Entra object id — a GUID that
    // resolves to a person via `az ad user show --id`, and carries no more personal data than the
    // opaque alternative does.
    private string ResolveUserId() => ResolveUserId(_httpContextAccessor?.HttpContext?.User);

    // Same rule applied to an explicitly supplied principal, so callers that already hold one (the
    // authorisation policy handler) attribute identically to those relying on the ambient context.
    private static string ResolveUserId(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        // The raw subject remains the fallback rather than being dropped: a token shape carrying no
        // object id would otherwise attribute to "unknown", and a consistent-but-opaque key is worth
        // more than none. It is the fallback and not the primary because an Entra v2 `sub` cannot be
        // resolved to a person — see CallerIdentity.
        return CallerIdentity.TryGetObjectId(user)
            ?? user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "unknown";
    }

    // Log the path only — strips the query string so filter values (which may contain customer
    // data) never land in the audit log. The record id in the path is fine and is the point.
    private static string ResourcePath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
}
