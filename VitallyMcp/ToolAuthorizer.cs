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
///      take effect within the cache window regardless of token age. The resolver answers from its
///      own fresh cache, and on a Graph failure from that caller's last known-good set for up to
///      <see cref="ToolAuthorizationOptions.LiveGroupStaleSeconds"/>.
///   2. Otherwise — the live check is off, no object id could be determined, or the resolver has
///      nothing usable at all — the token's permission claim / scope.
///
/// So the effective tiers are: fresh Graph → stale Graph → token claim → deny. The claim tier is the
/// Auth0 post-login Action's <c>permissions</c> claim, which still authorises correctly today; it
/// becomes permanently empty at the Entra cutover, and removing it there is #108's job. Until then
/// the tier below the stale cache is a working fallback, not a formality — which is why the stale
/// cache was added beneath it rather than in place of it.
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
        // Deployment-level read-only kill switch: deny every mutating verb regardless of RBAC
        // state, NoAuth, or token permissions. Checked before the Enabled/NoAuth gate so a
        // read-only deployment is locked even when per-user RBAC isn't configured.
        if (_options.ReadOnly && method != HttpMethod.Get)
        {
            throw new UnauthorizedAccessException(
                "This server is deployed in read-only mode; create, update and delete operations are disabled.");
        }

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

    /// <summary>
    /// Resolves whether <paramref name="user"/> effectively holds <paramref name="required"/>, using
    /// the live Entra group lookup when enabled and falling back to the token claim. Public so the
    /// ASP.NET Core authorization policy handler can share exactly this resolution — the discovery
    /// filter and the <see cref="VitallyService"/> enforcement backstop must never disagree.
    /// </summary>
    public async Task<bool> HasEffectivePermissionAsync(ClaimsPrincipal user, string required, CancellationToken cancellationToken = default)
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
                // live == null => the resolver had neither a fresh nor a usable stale result,
                // so fall through to the token claim. Not merely "Graph was slow": the stale window
                // has already been considered and declined by this point.
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

        // JwtBearer's default inbound claim mapping renames "sub" to ClaimTypes.NameIdentifier, so
        // check both — otherwise the object id is never found in production and the live lookup is
        // silently skipped (falling back to the frozen token claim).
        var sub = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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
    /// True when authorisation is switched off entirely (RBAC disabled or NoAuth dev mode), in which
    /// case discovery filtering must be a pass-through — otherwise local development would see an
    /// empty tool list. Async purely to keep the policy handler's call site uniform.
    /// </summary>
    public Task<bool> IsAuthorizationBypassedAsync() => Task.FromResult(!_options.Enabled || _noAuth);

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
