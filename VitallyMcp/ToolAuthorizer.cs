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
///
/// Permission resolution order:
///   1. If <see cref="ToolAuthorizationOptions.LiveGroupCheck"/> is on, the caller's <b>live</b>
///      Entra group membership (via <see cref="IGroupPermissionResolver"/>) — so group changes
///      take effect within the cache window regardless of token age.
///   2. Otherwise (or if the live lookup is unavailable) the token's permission claim / scope.
/// </summary>
public class ToolAuthorizer
{
    private readonly ToolAuthorizationOptions _options;
    private readonly bool _noAuth;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IGroupPermissionResolver? _groupResolver;

    public ToolAuthorizer(
        IOptions<ToolAuthorizationOptions> options,
        IOptions<OAuthOptions> oauth,
        IHttpContextAccessor? httpContextAccessor = null,
        IGroupPermissionResolver? groupResolver = null)
    {
        _options = options.Value;
        _noAuth = oauth.Value.NoAuth;
        _httpContextAccessor = httpContextAccessor;
        _groupResolver = groupResolver;
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> if the current caller lacks the permission
    /// required for the given HTTP verb. No-op when authorisation is disabled or in NoAuth dev mode.
    /// </summary>
    public async Task EnsureAuthorizedAsync(HttpMethod method, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || _noAuth)
        {
            return;
        }

        var required = RequiredPermission(method);
        var user = _httpContextAccessor?.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true
            || !await HasEffectivePermissionAsync(user, required, cancellationToken))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: this operation requires the '{required}' permission, which your token does not grant. "
                + "Contact the Infrastructure team if you need this access.");
        }
    }

    private async Task<bool> HasEffectivePermissionAsync(ClaimsPrincipal user, string required, CancellationToken cancellationToken)
    {
        if (_options.LiveGroupCheck && _groupResolver is not null)
        {
            var objectId = ExtractObjectId(user);
            if (objectId is not null)
            {
                var live = await _groupResolver.TryResolvePermissionsAsync(objectId, cancellationToken);
                if (live is not null)
                {
                    // Authoritative when the live lookup succeeds (empty set => deny).
                    return live.Contains(required);
                }
                // live == null => lookup unavailable; fall through to the token claim.
            }
        }

        return HasPermission(user, required, _options.CustomPermissionsClaim);
    }

    /// <summary>
    /// Extracts the user's Entra object id (GUID) for the Graph lookup: the <c>oid</c> claim if
    /// present, else the trailing GUID of the <c>sub</c> (Auth0 federated subjects are shaped
    /// <c>waad|connection|{objectId}</c>). Returns null if no GUID can be determined.
    /// </summary>
    private static string? ExtractObjectId(ClaimsPrincipal user)
    {
        var oid = user.FindFirst("oid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        if (!string.IsNullOrWhiteSpace(oid) && Guid.TryParse(oid, out _))
        {
            return oid;
        }

        var sub = user.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
        {
            var last = sub.Split('|', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null && Guid.TryParse(last, out _))
            {
                return last;
            }
        }

        return null;
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
        if (user.FindAll("permissions").Any(c => string.Equals(c.Value, required, StringComparison.Ordinal)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(customClaimType)
            && user.FindAll(customClaimType).Any(c => string.Equals(c.Value, required, StringComparison.Ordinal)))
        {
            return true;
        }

        var scope = user.FindFirst("scope")?.Value;
        return !string.IsNullOrEmpty(scope)
            && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(s => string.Equals(s, required, StringComparison.Ordinal));
    }
}
