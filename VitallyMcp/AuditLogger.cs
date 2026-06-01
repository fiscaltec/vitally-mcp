using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Emits per-user audit records for Vitally actions. Called from <see cref="VitallyService.SendAsync"/>
/// so every tool is covered in one place. Records the authenticated identity as the stable subject
/// id (the <c>sub</c> claim — an opaque Entra object id, resolvable to a person in Entra but not
/// itself PII), the HTTP verb, target resource path and outcome. Deliberately logs neither the
/// user's email nor the request body, keeping personal data out of telemetry while remaining fully
/// attributable. Uses structured logging so the named properties surface as queryable dimensions
/// in Application Insights / Log Analytics.
/// </summary>
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

    // Resolve the stable, attributable actor identity: the Entra subject id. This is an opaque
    // object id (resolvable to a person in Entra) rather than the user's email, so the audit trail
    // stays fully attributable without scattering personal data through telemetry.
    private string ResolveUserId()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return "anonymous";
        }

        return user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "unknown";
    }

    // Log the path only — strips the query string so filter values (which may contain customer
    // data) never land in the audit log. The record id in the path is fine and is the point.
    private static string ResourcePath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
}
