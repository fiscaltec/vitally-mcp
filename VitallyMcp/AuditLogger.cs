using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Emits per-user audit records for Vitally actions. Called from <see cref="VitallyService.SendAsync"/>
/// so every tool is covered in one place. Records the caller's Entra <b>object id</b> (see
/// <see cref="CallerIdentity"/>), the HTTP verb, target resource path and outcome. Deliberately logs
/// neither the user's email nor the request body, keeping personal data out of telemetry while
/// remaining fully attributable. Uses structured logging so the named properties surface as queryable
/// dimensions in Application Insights / Log Analytics.
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
