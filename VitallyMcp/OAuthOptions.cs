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
}
