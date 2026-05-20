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
}
