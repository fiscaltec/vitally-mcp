using ModelContextProtocol.Authentication;

namespace VitallyMcp;

/// <summary>
/// Builds the RFC 9728 protected-resource metadata document from <see cref="OAuthOptions"/>.
/// Extracted from Program.cs so the same document is served from both well-known paths and can be
/// asserted directly in tests.
/// </summary>
public static class ProtectedResourceMetadataBuilder
{
    /// <summary>Canonical metadata path. RFC 9728 also allows a resource-path suffix (…/mcp).</summary>
    public const string MetadataPath = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// OIDC scopes advertised to clients so they can request them at the authorize step. The API
    /// scope is appended by <see cref="ScopesFor"/>, because its spelling depends on the upstream
    /// provider.
    /// </summary>
    public static readonly string[] BaseScopes = ["openid", "profile", "email", "offline_access"];

    /// <summary>
    /// The advertised scope list: the OIDC scopes plus this server's API scope, named the way the
    /// upstream provider will accept it. Entra rejects a bare scope name on a custom API — it must
    /// carry the App ID URI prefix — so advertising the short form to a client that builds its
    /// request from this list would break the flow before sign-in.
    /// </summary>
    public static string[] ScopesFor(OAuthOptions oauth) =>
        [.. BaseScopes, oauth.TerminatesResourceParameter ? oauth.UpstreamResourceScope : "mcp.access"];

    public static ProtectedResourceMetadata Build(OAuthOptions oauth, string serverBaseUrl)
    {
        // When the OAuth proxy is active we are the authorization server clients talk to (so we can
        // intercept registration); otherwise point straight at the upstream authority.
        var authorizationServer = string.IsNullOrWhiteSpace(oauth.SharedClientId)
            ? oauth.Authority?.TrimEnd('/')
            : serverBaseUrl;

        return new ProtectedResourceMetadata
        {
            Resource = oauth.PublishedResourceIdentifier,
            // Collection-expression assignment (not the nested `= { ... }` initialiser syntax) is
            // deliberate: BearerMethodsSupported already defaults to a pre-populated ["header"]
            // list on this SDK type, so `= { "header" }` would append and leave a duplicate entry.
            // Assigning replaces the list outright for all three.
            AuthorizationServers = [authorizationServer ?? string.Empty],
            BearerMethodsSupported = ["header"],
            ScopesSupported = [.. ScopesFor(oauth)],
            ResourceName = "Vitally MCP"
        };
    }
}
