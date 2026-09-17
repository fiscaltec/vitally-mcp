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
/// <b>Three record shapes, not one</b>, because they are emitted at different points:
/// <list type="bullet">
///   <item><see cref="LogAction"/> — from <see cref="VitallyService.SendAsync"/> after each upstream
///     response: identity, verb, resource path (query string stripped), status.</item>
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
/// What this class does <i>today</i> is unchanged and narrower than that design: object id, verb,
/// resource path with the query string stripped, and status. The tool-call record — arguments,
/// returned record ids, result count, correlation id — is not implemented. Design:
/// <c>docs/superpowers/specs/2026-09-17-logging-observability-design.md</c>.
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

    public AuditLogger(
        IOptions<AuditOptions> options,
        ILogger<AuditLogger> logger,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _options = options.Value;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Records a completed action (after the upstream response, success or failure).</summary>
    public void LogAction(HttpMethod method, string url, int statusCode)
    {
        if (!_options.Enabled)
        {
            return;
        }
        if (method == HttpMethod.Get && !_options.IncludeReads)
        {
            return;
        }

        _logger.LogInformation(
            "Vitally audit: {AuditUserId} {HttpMethod} {VitallyResource} -> {StatusCode}",
            ResolveUserId(), method.Method, ResourcePath(url), statusCode);
    }

    /// <summary>Records an action the caller was not permitted to perform (RBAC denial).</summary>
    public void LogDenied(HttpMethod method, string url)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _logger.LogWarning(
            "Vitally audit: {AuditUserId} DENIED {HttpMethod} {VitallyResource}",
            ResolveUserId(), method.Method, ResourcePath(url));
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
    /// rather than relying on the ambient HTTP context. Records only the opaque subject id, the tool
    /// name and the permission that was required — never the caller's email, and never the call
    /// arguments (they can carry customer PII).
    /// </para>
    /// </summary>
    public void LogToolCallDenied(ClaimsPrincipal? user, string? toolName, string requiredPermission)
    {
        if (!_options.Enabled)
        {
            return;
        }

        _logger.LogWarning(
            "Vitally audit: {AuditUserId} DENIED tools/call {McpToolName} (requires {RequiredPermission})",
            ResolveUserId(user), toolName ?? "unknown", requiredPermission);
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
