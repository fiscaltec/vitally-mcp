using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Enforces <see cref="ToolAuthorizationOptions"/> against the authenticated caller. Called from
/// <see cref="VitallyService.SendAsync"/> — the single point every Vitally API call funnels
/// through — so all ~92 tools are covered without per-tool annotation. Because every read
/// (including search) is a GET and every mutation is a POST/PUT/DELETE, the HTTP verb is a
/// faithful proxy for the tool's read/write/delete tier.
/// </summary>
public class ToolAuthorizer
{
    private readonly ToolAuthorizationOptions _options;
    private readonly bool _noAuth;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    public ToolAuthorizer(
        IOptions<ToolAuthorizationOptions> options,
        IOptions<OAuthOptions> oauth,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _options = options.Value;
        _noAuth = oauth.Value.NoAuth;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> if the current caller lacks the permission
    /// required for the given HTTP verb. No-op when authorisation is disabled or in NoAuth dev mode.
    /// </summary>
    public void EnsureAuthorized(HttpMethod method)
    {
        if (!_options.Enabled || _noAuth)
        {
            return;
        }

        var required = RequiredPermission(method);
        var user = _httpContextAccessor?.HttpContext?.User;

        if (user is null || !HasPermission(user, required, _options.CustomPermissionsClaim))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: this operation requires the '{required}' permission, which your token does not grant. "
                + "Contact the Infrastructure team if you need this access.");
        }
    }

    /// <summary>
    /// Maps an HTTP verb to the required permission. Unknown verbs fall back to the most
    /// restrictive (delete) permission so an unexpected method can never be the soft option.
    /// </summary>
    public string RequiredPermission(HttpMethod method)
    {
        if (method == HttpMethod.Get)
        {
            return _options.ReadPermission;
        }
        if (method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch)
        {
            return _options.WritePermission;
        }
        return _options.DeletePermission;
    }

    /// <summary>
    /// True if the principal carries <paramref name="required"/> as an Auth0 RBAC <c>permissions</c>
    /// claim entry, as an entry in the optional <paramref name="customClaimType"/> claim, or as a
    /// space-delimited value in the <c>scope</c> claim.
    /// </summary>
    public static bool HasPermission(ClaimsPrincipal user, string required, string? customClaimType = null)
    {
        foreach (var claim in user.FindAll("permissions"))
        {
            if (string.Equals(claim.Value, required, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(customClaimType))
        {
            foreach (var claim in user.FindAll(customClaimType))
            {
                if (string.Equals(claim.Value, required, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        var scope = user.FindFirst("scope")?.Value;
        if (!string.IsNullOrEmpty(scope))
        {
            foreach (var s in scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(s, required, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
