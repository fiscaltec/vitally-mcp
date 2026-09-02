namespace VitallyMcp;

/// <summary>
/// OAuth / OIDC configuration for the Vitally MCP server. Provider-agnostic — works with any
/// OIDC-compliant authorization server (Auth0, Microsoft Entra, Keycloak, Okta, ...).
/// </summary>
public class OAuthOptions
{
    public const string SectionName = "OAuth";

    /// <summary>
    /// Issuer URL of the authorization server, used as JwtBearer Authority. Must include scheme.
    /// Examples: <c>https://fiscal-it.uk.auth0.com/</c>, <c>https://login.microsoftonline.com/{tenant-id}/v2.0</c>.
    /// </summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Value to validate the JWT <c>aud</c> claim against — the Entra App ID URI, which carries
    /// <b>no</b> trailing slash because Entra refuses to register one on <c>identifierUris</c>.
    /// <see cref="ValidAudiences"/> also accepts <see cref="SharedClientId"/>, which is what a v2
    /// token actually names.
    /// </summary>
    /// <remarks>
    /// <b>Not the same value as <see cref="Resource"/>, and must not be reconciled with it.</b> They
    /// were equal under Auth0 by coincidence — that identifier happened to carry a trailing slash —
    /// and differ by exactly that slash under Entra. Making them agree breaks token validation in one
    /// direction and the RFC 9728 document in the other. On staging they differ by host as well,
    /// because one app registration serves both origins.
    /// </remarks>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Canonical resource identifier published in <c>/.well-known/oauth-protected-resource</c>'s
    /// <c>resource</c> field per RFC 9728. Clients (e.g. Claude Code) validate this matches the
    /// server URL or origin, then send it as the <c>resource=</c> parameter on their OAuth
    /// authorize call (RFC 8707). Falls back to <see cref="Audience"/> if left empty.
    /// </summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>
    /// When true, JWT authentication is skipped. Local development only — never enable in production.
    /// </summary>
    public bool NoAuth { get; set; }

    /// <summary>
    /// The canonical resource identifier this server actually publishes: <see cref="Resource"/>,
    /// falling back to <see cref="Audience"/>. Both the RFC 9728 document and
    /// <see cref="IsResourceIndicatorAllowed"/> read it, so the value clients are told to send is
    /// by construction the value their <c>resource</c> parameter is validated against.
    /// </summary>
    public string PublishedResourceIdentifier =>
        (string.IsNullOrWhiteSpace(Resource) ? Audience : Resource)?.Trim() ?? string.Empty;

    /// <summary>
    /// Audience values the JWT <c>aud</c> claim may carry: <see cref="Audience"/> and, when set,
    /// <see cref="SharedClientId"/>. Both, because which one a provider mints is a property of the
    /// token version rather than of our configuration — an Entra <b>v1</b> access token names the
    /// App ID URI, a <b>v2</b> token names the resource application's appId GUID, and this
    /// registration is both the client and the resource so the two are the same object. Accepting
    /// each removes the question rather than betting on it.
    /// </summary>
    /// <remarks>
    /// The consequence to be aware of: an <i>ID</i> token for this app also carries the appId GUID as
    /// <c>aud</c>, so one presented as a bearer token would pass audience validation. That is not an
    /// escalation — it is issued to the same client for the same user, and entitlement is still
    /// resolved from that user's live Entra group membership, so an ID token buys exactly the tier
    /// its own access token would. Narrowing it would mean requiring <c>scp</c>, which is provider-
    /// specific in a way this class deliberately is not.
    /// </remarks>
    public IReadOnlyList<string> ValidAudiences =>
        new[] { Audience, SharedClientId }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// True when <see cref="UpstreamResourceScope"/> is configured, i.e. the proxy terminates the
    /// RFC 8707 <c>resource</c> parameter at this façade rather than relaying it upstream.
    /// </summary>
    public bool TerminatesResourceParameter => !string.IsNullOrWhiteSpace(UpstreamResourceScope);

    /// <summary>
    /// Merges <see cref="UpstreamResourceScope"/> into the <c>scope</c> value a client sent, so the
    /// upstream request names this server's API even when the client only asked for the OIDC scopes.
    /// Returns the client's value unchanged if it already names it, and the bare scope when the
    /// client sent none.
    /// </summary>
    /// <remarks>
    /// The comparison is case-insensitive although RFC 6749 §3.3 makes scope tokens case-sensitive:
    /// a duplicate here is a request that fails upstream, so tolerating a casing difference is
    /// strictly better than emitting the same scope twice in two spellings.
    /// </remarks>
    public string MergeUpstreamScope(string? requestedScope)
    {
        var scope = UpstreamResourceScope;
        var requested = (requestedScope ?? string.Empty).Trim();
        if (requested.Length == 0)
        {
            return scope;
        }

        var tokens = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Contains(scope, StringComparer.OrdinalIgnoreCase)
            ? string.Join(' ', tokens)
            : string.Join(' ', tokens.Append(scope));
    }

    /// <summary>
    /// Canonical public origin of this server (scheme + host, no trailing path), e.g.
    /// <c>https://vitally.fiscaltec.com</c>. When set, the <c>/.well-known/*</c> metadata documents
    /// and the OAuth proxy's own callback URL are built from this value instead of the request's
    /// <c>Host</c> header. Set it in production so a spoofed/forwarded <c>Host</c> can never steer a
    /// client's <c>authorization_endpoint</c>/<c>token_endpoint</c> at an attacker. Leave empty in
    /// local development to fall back to the request scheme+host (so loopback URLs still work).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// OAuth client_id of the pre-registered application that all MCP clients converge on. When set,
    /// the server intercepts RFC 7591 Dynamic Client Registration calls and returns this client_id to
    /// every caller — eliminating per-session client proliferation at the provider and the
    /// per-session consent that DCR clients otherwise trigger. Leave empty to fall through to the
    /// provider's own DCR endpoint, if it has one.
    /// </summary>
    /// <remarks>
    /// Under Entra this is the <c>Vitally MCP</c> app registration, which is <b>both</b> the OAuth
    /// client and the API resource — hence its appearing in <see cref="ValidAudiences"/>. Its
    /// redirect URIs are the fixed <c>/oauth/callback</c> of each origin, not the clients' own
    /// loopback URIs: the proxy substitutes its own and reverses the substitution at the callback,
    /// which is what lets ephemeral loopback ports coexist with one registration. See
    /// <c>docs/runbooks/entra-app-registration.md</c>.
    /// </remarks>
    public string SharedClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client secret of the <see cref="SharedClientId"/> application. Injected server-side by the
    /// OAuth proxy's /oauth/token endpoint when forwarding token requests upstream — so the shared
    /// app can be a confidential client without exposing the secret to MCP clients. Optional: leave
    /// empty for public-client mode. Sourced from the Key Vault secret <c>entra-mcp-client-secret</c>,
    /// which <b>expires</b>; see <c>docs/runbooks/entra-app-registration.md</c> for the rotation.
    /// </summary>
    public string SharedClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// Scope value that names this server's own API to the upstream provider — under Entra the App
    /// ID URI plus the exposed scope, e.g. <c>https://vitally.fiscaltec.com/mcp.access</c>. Empty by
    /// default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Setting this switches the OAuth proxy from <b>relaying</b> the RFC 8707 <c>resource</c>
    /// parameter to <b>terminating</b> it: <c>resource</c> is still validated against
    /// <see cref="PublishedResourceIdentifier"/> and a mismatch is still refused, but a matching
    /// value is then dropped at the façade instead of being forwarded, and this scope is merged into
    /// the upstream <c>scope</c> parameter in its place.
    /// </para>
    /// <para>
    /// The two halves are one setting on purpose, because neither is usable alone. Dropping
    /// <c>resource</c> without naming the resource some other way leaves the issued token bound to no
    /// audience at all — the proxy sends no <c>audience</c> parameter anywhere. Adding a
    /// resource-naming scope while still relaying <c>resource</c> is exactly the
    /// <c>AADSTS9010010</c> failure this exists to avoid: Entra matches <c>resource</c> exactly
    /// against a registered identifier and rejects the trailing-slash form MCP clients send, which
    /// <see cref="IsResourceIndicatorAllowed"/> deliberately accepts.
    /// </para>
    /// <para>
    /// So this is the provider switch, expressed as configuration rather than inferred from
    /// <see cref="Authority"/>: leave it empty on Auth0, where the tenant's Resource Parameter
    /// Compatibility Profile consumes the relayed <c>resource</c> and is the only thing binding the
    /// audience; set it on Entra. Keeping it configuration is what keeps a cutover rollback a
    /// revert of environment variables rather than a redeploy.
    /// </para>
    /// </remarks>
    public string UpstreamResourceScope { get; set; } = string.Empty;

    /// <summary>
    /// Allowlist of non-loopback redirect URIs that the OAuth proxy will accept on
    /// <c>/oauth/authorize</c> and <c>/oauth/register</c>. Loopback URIs (<c>http://localhost</c>,
    /// <c>http://127.0.0.1</c>, <c>http://[::1]</c>) on any port are always accepted per RFC 8252
    /// §7.3 — those don't need to be listed. Add cloud-hosted MCP callbacks here, e.g.
    /// <c>https://claude.ai/api/mcp/auth_callback</c>.
    /// Empty by default — set explicitly when a hosted MCP client (not a local app) needs to
    /// complete the flow. Without the allowlist, only local clients can authenticate.
    /// </summary>
    public string[] AllowedClientRedirectUris { get; set; } = [];

    /// <summary>
    /// Fail-fast configuration sanity check. Wired via <c>PostConfigure</c> in <c>Program.cs</c>,
    /// then triggered immediately after <c>builder.Build()</c> by resolving
    /// <c>IOptions&lt;OAuthOptions&gt;</c>, so misconfiguration throws at startup rather than at
    /// the first failed token validation.
    /// </summary>
    public void Validate()
    {
        Authority = Authority?.Trim() ?? string.Empty;
        Audience = Audience?.Trim() ?? string.Empty;
        Resource = Resource?.Trim() ?? string.Empty;
        SharedClientId = SharedClientId?.Trim() ?? string.Empty;
        SharedClientSecret = SharedClientSecret?.Trim() ?? string.Empty;
        UpstreamResourceScope = UpstreamResourceScope?.Trim() ?? string.Empty;
        PublicBaseUrl = (PublicBaseUrl?.Trim() ?? string.Empty).TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(PublicBaseUrl)
            && (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicUri) || publicUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"OAuth:PublicBaseUrl must be an absolute https URI (got '{PublicBaseUrl}').");
        }

        // The OAuth proxy endpoints (/oauth/authorize, /oauth/token, /.well-known/*)
        // use Authority to build upstream URLs even when JWT validation is skipped, so
        // Authority is required whenever the proxy is enabled — including NoAuth dev mode.
        var proxyEnabled = !string.IsNullOrWhiteSpace(SharedClientId);

        if (NoAuth && !proxyEnabled)
        {
            // Dev-mode bypass with no proxy: nothing else to validate.
            return;
        }

        if (string.IsNullOrWhiteSpace(Authority))
        {
            throw new InvalidOperationException(
                NoAuth
                    ? "OAuth:Authority is required when OAuth:SharedClientId is set — the OAuth proxy uses it to build upstream URLs."
                    : "OAuth:Authority is required when OAuth:NoAuth is false.");
        }
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"OAuth:Authority must be an absolute https URI (got '{Authority}').");
        }

        // Audience is only used by JwtBearer, which is skipped when NoAuth=true.
        if (!NoAuth && string.IsNullOrWhiteSpace(Audience))
        {
            throw new InvalidOperationException("OAuth:Audience is required when OAuth:NoAuth is false.");
        }

        // PublishedResourceIdentifier is published as the RFC 9728 `resource` *and* is what an
        // incoming RFC 8707 `resource` parameter is validated against, so a value that cannot be
        // parsed would reject every request carrying the parameter — an authentication outage
        // discovered at the first sign-in rather than at boot. An Entra-style client-ID GUID is
        // a perfectly good `aud` but not a resource identifier, and lands here when `Resource`
        // is left unset; set `Resource` to the server origin in that case.
        //
        // The scheme is checked, not just absoluteness: Uri.TryCreate("/mcp", Absolute) succeeds on
        // Unix as file:///mcp and fails on Windows, so an absoluteness-only check would be inert on
        // the Linux containers this actually runs on (CI caught it). http is allowed alongside https
        // for loopback development; production carries an https origin.
        var publishedResource = PublishedResourceIdentifier;
        if (!string.IsNullOrWhiteSpace(publishedResource)
            && (!Uri.TryCreate(publishedResource, UriKind.Absolute, out var resourceUri)
                || (resourceUri.Scheme != Uri.UriSchemeHttps && resourceUri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(resourceUri.Fragment)))
        {
            throw new InvalidOperationException(
                $"OAuth:Resource (or OAuth:Audience, which stands in for it when Resource is empty) must be an absolute http(s) URI with no fragment (got '{publishedResource}').");
        }

        if (!string.IsNullOrWhiteSpace(SharedClientSecret) && string.IsNullOrWhiteSpace(SharedClientId))
        {
            throw new InvalidOperationException("OAuth:SharedClientSecret requires OAuth:SharedClientId to also be set.");
        }

        // A single scope token, checked at boot rather than at the first sign-in. Whitespace would
        // smuggle extra scopes into every authorize request, and this value names exactly one
        // resource — the one whose `resource` parameter it replaces. Entra's own form is an absolute
        // URI plus the scope name, but the check stays shape-agnostic so a different provider's
        // convention (a bare `api://.../scope`, or a plain name) is not rejected on principle.
        if (TerminatesResourceParameter && UpstreamResourceScope.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                $"OAuth:UpstreamResourceScope must be a single scope token with no whitespace (got '{UpstreamResourceScope}').");
        }

        // Normalise the allowlist: trim, strip trailing slashes (we match by prefix below so
        // both stored and incoming values need the same shape), and fail fast on invalid URIs
        // rather than at first request.
        AllowedClientRedirectUris = (AllowedClientRedirectUris ?? [])
            .Select(u => u?.Trim() ?? string.Empty)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.TrimEnd('/'))
            .ToArray();

        // HTTPS-only per OAuth 2.0 Security BCP — non-loopback redirect URIs must be over TLS
        // to prevent authorisation-code interception on the network. Loopback http is handled
        // separately by IsRedirectUriAllowed and never needs to appear in this list.
        var invalid = AllowedClientRedirectUris.FirstOrDefault(entry =>
            !Uri.TryCreate(entry, UriKind.Absolute, out var allowed)
            || allowed.Scheme != Uri.UriSchemeHttps);
        if (invalid is not null)
        {
            throw new InvalidOperationException(
                $"OAuth:AllowedClientRedirectUris entries must be absolute https URIs (got '{invalid}'). Loopback http redirects do not need to be listed — they are allowed automatically per RFC 8252.");
        }
    }

    /// <summary>
    /// Returns true if <paramref name="redirectUri"/> is acceptable as an OAuth proxy redirect
    /// target. Loopback http URIs on any port are always allowed (RFC 8252 §7.3 covers MCP
    /// clients like Claude Code, VS Code, Cursor that bind ephemeral local ports). Non-loopback
    /// URIs must prefix-match an entry in <see cref="AllowedClientRedirectUris"/>. URIs that
    /// contain a fragment are always rejected per OAuth 2.0 §3.1.2 — the proxy appends
    /// <c>?state=&amp;code=</c> on redirect-back, which a fragment would silently break by
    /// trapping those params on the client side of the URL.
    /// </summary>
    public bool IsRedirectUriAllowed(string redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return false;
        }

        // Reject whitespace-padded input outright. Uri.TryCreate tolerates leading whitespace
        // and parses successfully, but the allowlist string comparisons below would still see
        // the original (un-normalised) value and reject what looks superficially like a valid
        // match. Better to reject explicitly than silently normalise — a well-behaved client
        // never sends padding.
        if (redirectUri.Length != redirectUri.Trim().Length)
        {
            return false;
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Fragments are forbidden in redirect_uri per OAuth 2.0 §3.1.2, and they would in
        // practice corrupt our callback append (the code+state get trapped in the fragment
        // and never reach the client).
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // RFC 8252 loopback redirect: http on localhost or any IPv4/IPv6 loopback address on
        // any port. Only http (not https) is the recognised scheme for loopback callbacks per
        // the RFC, because native clients can't reasonably provision a TLS cert on localhost.
        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host))
        {
            return true;
        }

        // Non-loopback: component-wise match against the configured allowlist. Per RFC 3986
        // §6.2.2 scheme and host are case-insensitive but path/query are case-sensitive, so
        // we compare components separately rather than doing a single OrdinalIgnoreCase string
        // match (which would let an attacker route through e.g. "/AUTH_CALLBACK" if the server
        // treats that as a different endpoint than "/auth_callback"). The path comparison uses
        // a strict path-segment prefix (allowed-path or allowed-path + "/") so spoofs like
        // "/api/mcp/auth_callback.evil.com" or "/api/mcp/auth_callback_extra" still fail.
        return AllowedClientRedirectUris.Any(allowed =>
        {
            if (!Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri))
            {
                return false; // Validate() ensures every entry parses, so this is belt-and-braces.
            }

            if (!string.Equals(uri.Scheme, allowedUri.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Host, allowedUri.Host, StringComparison.OrdinalIgnoreCase)
                || uri.Port != allowedUri.Port)
            {
                return false;
            }

            var incomingPath = uri.AbsolutePath.TrimEnd('/');
            var allowedPath = allowedUri.AbsolutePath.TrimEnd('/');

            // Special-case root-path entries: TrimEnd reduces "/" to "" for both sides, and
            // the StartsWith("" + "/") check below would then match every path on the host —
            // turning a "https://example.com" allowlist entry into an unintended wildcard.
            // Require an exact root match in that case (incoming path also empty/root).
            if (allowedPath.Length == 0)
            {
                return incomingPath.Length == 0;
            }

            return incomingPath.Equals(allowedPath, StringComparison.Ordinal)
                || incomingPath.StartsWith(allowedPath + "/", StringComparison.Ordinal);
        });
    }


    /// <summary>
    /// Returns true if <paramref name="resource"/> names the resource this server publishes, so an
    /// RFC 8707 <c>resource</c> parameter may be honoured. The parameter is an audience-binding
    /// control: whatever the caller sends here is what the token's <c>aud</c> ends up naming, so a
    /// value we never published must be refused rather than passed on or quietly dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compared component-wise rather than as one string, per RFC 3986 §6.2.2: scheme and host are
    /// case-insensitive, path and query are not. A single trailing slash is tolerated on either
    /// side — and that tolerance is load-bearing, not cosmetic. Entra refuses to register an
    /// <c>identifierUris</c> value ending in a slash, while Claude Code normalises a bare-host
    /// resource *to* the trailing-slash form, so the two forms have to be treated as one name.
    /// Nothing else is normalised: a differing port, scheme or path is a different resource.
    /// </para>
    /// <para>
    /// With no identifier configured at all — neither <see cref="Resource"/> nor
    /// <see cref="Audience"/>, which <see cref="Validate"/> only permits when <see cref="NoAuth"/>
    /// is true — there is nothing to compare against and no audience binding to protect, so every
    /// value is accepted. Production always has <see cref="Audience"/>.
    /// </para>
    /// </remarks>
    public bool IsResourceIndicatorAllowed(string resource)
    {
        var published = PublishedResourceIdentifier;
        if (string.IsNullOrWhiteSpace(published))
        {
            return true;
        }

        // A present-but-empty `resource` binds nothing, so it is malformed rather than absent —
        // callers must only reach here when the parameter was actually sent.
        if (string.IsNullOrWhiteSpace(resource))
        {
            return false;
        }

        // As in IsRedirectUriAllowed: Uri.TryCreate tolerates padding, the comparisons below would
        // not, so reject it outright rather than silently normalising a client's malformed input.
        if (resource.Length != resource.Trim().Length)
        {
            return false;
        }

        if (!Uri.TryCreate(resource, UriKind.Absolute, out var requested)
            || !Uri.TryCreate(published, UriKind.Absolute, out var expected))
        {
            return false;
        }

        // RFC 8707 §2 — a resource indicator must not carry a fragment.
        if (!string.IsNullOrEmpty(requested.Fragment))
        {
            return false;
        }

        return string.Equals(requested.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(requested.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && requested.Port == expected.Port
            && string.Equals(WithoutOneTrailingSlash(requested.AbsolutePath), WithoutOneTrailingSlash(expected.AbsolutePath), StringComparison.Ordinal)
            && string.Equals(requested.Query, expected.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Drops at most one trailing slash. Deliberately not <c>TrimEnd('/')</c>: the tolerance exists
    /// because Entra and Claude Code disagree by exactly one slash, and <c>/mcp//</c> is a different
    /// path per RFC 3986 rather than another spelling of the same resource.
    /// </summary>
    private static string WithoutOneTrailingSlash(string path) =>
        path.EndsWith('/') ? path[..^1] : path;
    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        // Covers the full IPv4 127.0.0.0/8 loopback range plus IPv6 ::1.
        // Uri.Host strips the brackets from IPv6 literals, so "::1" (not "[::1]") is what
        // arrives here for an input like http://[::1]:8080/.
        || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));
}
