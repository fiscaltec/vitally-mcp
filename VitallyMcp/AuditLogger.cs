using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Emits per-user audit records for Vitally actions. Called from <see cref="VitallyService.SendAsync"/>
/// so every tool is covered in one place. Records the authenticated identity, HTTP verb, target
/// resource path and outcome — deliberately never the request body, which can carry PII (traits,
/// transcripts). Uses structured logging so the named properties surface as queryable dimensions
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

        var (user, id) = ResolveUser();
        _logger.LogInformation(
            "Vitally audit: {AuditUser} ({AuditUserId}) {HttpMethod} {VitallyResource} -> {StatusCode}",
            user, id, method.Method, ResourcePath(url), statusCode);
    }

    /// <summary>Records an action the caller was not permitted to perform (RBAC denial).</summary>
    public void LogDenied(HttpMethod method, string url)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var (user, id) = ResolveUser();
        _logger.LogWarning(
            "Vitally audit: {AuditUser} ({AuditUserId}) DENIED {HttpMethod} {VitallyResource}",
            user, id, method.Method, ResourcePath(url));
    }

    private (string User, string Id) ResolveUser()
    {
        var user = _httpContextAccessor?.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return ("anonymous", "-");
        }

        var id = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? "-";

        var display = user.FindFirst("email")?.Value
            ?? user.FindFirst(ClaimTypes.Email)?.Value
            ?? user.Claims.FirstOrDefault(c => c.Type.EndsWith("/email", StringComparison.Ordinal))?.Value
            ?? user.FindFirst("name")?.Value
            ?? id;

        return (display, id);
    }

    // Log the path only — strips the query string so filter values (which may contain customer
    // data) never land in the audit log. The record id in the path is fine and is the point.
    private static string ResourcePath(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
}
