using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Enforces <see cref="ToolAuthorizationOptions"/> against the authenticated caller. Called from
/// <see cref="VitallyService.SendAsync"/> — the single point every Vitally API call funnels
/// through — so all ~92 tools are covered without per-tool annotation. Because every read
/// (including search) is a GET and every mutation is a POST/PUT/DELETE, the HTTP verb is a
/// faithful proxy for the tool's read/write/delete tier.
///
/// Permission resolution has two modes, chosen by
/// <see cref="ToolAuthorizationOptions.LiveGroupCheck"/> and never mixed:
///   1. <b>Live group check on</b> (every deployed target): permissions come from the caller's
///      current Entra group membership via <see cref="IGroupPermissionResolver"/>, which answers
///      from its own fresh cache and, when a Graph call fails, from that caller's last known-good
///      set for up to <see cref="ToolAuthorizationOptions.LiveGroupStaleSeconds"/>. So the tiers are
///      <b>fresh Graph → stale Graph → deny</b>. Nothing in the token can grant access.
///   2. <b>Live group check off</b>: the token's permission claim / scope, for deployments with no
///      Graph reachability that accept a token-frozen entitlement.
///
/// <para>The token claim used to sit beneath the stale cache as a third tier in mode 1. It was the
/// Auth0 post-login Action's <c>permissions</c> claim, and while Auth0 issued the tokens it genuinely
/// authorised — which is why #106 added the stale cache beneath it rather than in place of it. The
/// Entra cutover (#108) retired the Action, so that claim is now permanently absent and a
/// fall-through to it could only ever deny. Keeping it would have left code that reads like a working
/// fallback and behaves like a silent denial; the explicit deny below says what actually happens, and
/// logs why.</para>
/// </summary>
public class ToolAuthorizer
{
    private readonly ToolAuthorizationOptions _options;
    private readonly bool _noAuth;
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private readonly IGroupPermissionResolver? _groupResolver;
    private readonly ILogger<ToolAuthorizer>? _logger;

    public ToolAuthorizer(
        IOptions<ToolAuthorizationOptions> options,
        IOptions<OAuthOptions> oauth,
        IHttpContextAccessor? httpContextAccessor = null,
        IGroupPermissionResolver? groupResolver = null,
        ILogger<ToolAuthorizer>? logger = null)
    {
        _options = options.Value;
        _noAuth = oauth.Value.NoAuth;
        _httpContextAccessor = httpContextAccessor;
        _groupResolver = groupResolver;
        _logger = logger;
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
    /// Resolves whether <paramref name="user"/> effectively holds <paramref name="required"/> — from
    /// the live Entra group lookup when it is enabled, and from the token claim only when it is not.
    /// There is no path from the former to the latter: see the class remarks. Public so the
    /// ASP.NET Core authorization policy handler can share exactly this resolution — the discovery
    /// filter and the <see cref="VitallyService"/> enforcement backstop must never disagree.
    /// </summary>
    public async Task<bool> HasEffectivePermissionAsync(ClaimsPrincipal user, string required, CancellationToken cancellationToken = default)
    {
        if (_options.LiveGroupCheck && _groupResolver is not null)
        {
            // Fail closed: with the live check on, Graph is the *only* source of entitlement, so
            // every way out of this block that is not an answer from Graph is a denial. Both are
            // logged rather than quietly returning false — after the cutover they are the only two
            // ways a correctly signed-in user can be refused everything, and neither is visible in
            // the audit record, which reports the denial and not its cause.
            var objectId = ExtractObjectId(user);
            if (objectId is null)
            {
                // An Entra v2 token always carries `oid`. Reaching here means the token is not
                // shaped as expected (or inbound claim mapping has changed), not that the user lacks
                // a tier — so it is worth its own line.
                _logger?.LogWarning(
                    "Denying '{RequiredPermission}': no Entra object id could be determined from the caller's token, "
                    + "and Authorization:LiveGroupCheck makes the live group lookup the only source of entitlement.",
                    required);
                return false;
            }

            var live = await _groupResolver.TryResolvePermissionsAsync(objectId, cancellationToken);
            if (live is null)
            {
                // The resolver had neither a fresh result nor a usable stale one — it has already
                // considered and declined the stale window by this point, and logged the underlying
                // Graph failure itself. Subject id only, never the caller's email (see AuditOptions).
                _logger?.LogWarning(
                    "Denying '{RequiredPermission}' for {UserObjectId}: the live group lookup returned nothing usable "
                    + "and there is no other source of entitlement.",
                    required,
                    objectId);
                return false;
            }

            // Authoritative when the live lookup succeeds (empty set => deny).
            return live.Contains(required);
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
    /// True if the principal carries <paramref name="required"/> as a <c>permissions</c> claim entry,
    /// as an entry in the optional <paramref name="customClaimType"/> claim, or as a space-delimited
    /// value in the <c>scope</c> claim.
    /// </summary>
    /// <remarks>
    /// Reached only when <see cref="ToolAuthorizationOptions.LiveGroupCheck"/> is off, where it is
    /// that mode's entire resolution rather than a fallback beneath the live check. See the class
    /// remarks for why there is no longer a fall-through from the live path to here.
    /// </remarks>
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
