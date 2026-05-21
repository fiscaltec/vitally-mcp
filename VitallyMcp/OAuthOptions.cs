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
    /// Value to validate the JWT <c>aud</c> claim against. Depends on the AS:
    /// for Auth0 it's the API identifier; for v2 Entra tokens it's typically the application
    /// (client) ID GUID.
    /// </summary>
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
    /// OAuth client_id of a pre-registered Auth0 native application that all MCP clients share.
    /// When set, the server intercepts RFC 7591 Dynamic Client Registration calls and returns this
    /// client_id to every caller — eliminating per-session client proliferation in Auth0 and the
    /// per-session manual API-grant clicks that DCR third-party clients otherwise require.
    /// The pre-registered Auth0 app must be native/public, first-party, with redirect URI
    /// <c>http://localhost</c> (loopback pattern matches any port), and have a client_grant on
    /// the Vitally MCP API. Leave empty to fall through to Auth0's native DCR endpoint.
    /// </summary>
    public string SharedClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client secret of the SharedClientId application. Injected server-side by the OAuth proxy's
    /// /oauth/token endpoint when forwarding token requests upstream — so the shared Auth0 app can
    /// be a confidential client (verifiable first-party = skip consent screen) without exposing the
    /// secret to MCP clients. Optional: leave empty for public-client mode (consent screen will show).
    /// </summary>
    public string SharedClientSecret { get; set; } = string.Empty;

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

        if (!string.IsNullOrWhiteSpace(SharedClientSecret) && string.IsNullOrWhiteSpace(SharedClientId))
        {
            throw new InvalidOperationException("OAuth:SharedClientSecret requires OAuth:SharedClientId to also be set.");
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

        // RFC 8252 loopback redirect: http://localhost / 127.0.0.1 / [::1] on any port.
        // Only http (not https) is the recognised scheme for loopback callbacks per the RFC,
        // because native clients can't reasonably provision a TLS cert on localhost.
        if (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host))
        {
            return true;
        }

        // Non-loopback: prefix-match against the configured allowlist. Prefix (rather than
        // equality) lets a single entry like "https://claude.ai/api/mcp/auth_callback" cover
        // the same URI with appended path segment or query that some clients add before
        // redirecting. The trailing-character check (`/`, `?`) prevents prefix spoofing like
        // "https://claude.ai/api/mcp/auth_callback.evil.com".
        var normalised = redirectUri.TrimEnd('/');
        return AllowedClientRedirectUris.Any(allowed =>
            normalised.Equals(allowed, StringComparison.OrdinalIgnoreCase)
            || normalised.StartsWith(allowed + "/", StringComparison.OrdinalIgnoreCase)
            || normalised.StartsWith(allowed + "?", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "[::1]"
        || host == "::1";
}
