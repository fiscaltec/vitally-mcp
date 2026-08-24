using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// The upstream authorization server's endpoints, as published by that server rather than
/// constructed by us. Every field is required: the OAuth proxy calls the first two on the wire and
/// republishes the last two to MCP clients in our RFC 8414 document, so a missing one is a
/// configuration error, not an optional extra.
/// </summary>
/// <param name="AuthorizationEndpoint">Upstream <c>/authorize</c> — where <c>/oauth/authorize</c> sends the user.</param>
/// <param name="TokenEndpoint">Upstream token endpoint — where <c>/oauth/token</c> forwards code and refresh grants.</param>
/// <param name="JwksUri">Signing-key set, republished verbatim to clients.</param>
/// <param name="UserInfoEndpoint">OIDC userinfo endpoint, republished verbatim to clients.</param>
public sealed record UpstreamOidcEndpoints(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string JwksUri,
    string UserInfoEndpoint);

/// <summary>
/// Resolves the upstream provider's endpoints from its OIDC discovery document instead of
/// string-concatenating provider-specific path shapes onto <see cref="OAuthOptions.Authority"/>.
/// </summary>
/// <remarks>
/// <para>
/// Concatenation only ever worked for Auth0. Entra hangs its endpoints off
/// <c>{authority}/oauth2/v2.0/…</c> while its issuer is <c>{authority}/v2.0</c>, and its
/// <c>userinfo_endpoint</c> lives on <c>graph.microsoft.com</c> entirely — so no choice of
/// <c>Authority</c> yields all four. The discovery <em>path</em> is the one part that is genuinely
/// standard (OIDC Discovery §4 / RFC 8414 §3), which is why concatenating that much is safe.
/// </para>
/// <para>
/// Two of the four are published to clients in <c>/.well-known/oauth-authorization-server</c>, so a
/// wrong value is advertised as fact rather than merely failing locally — hence the fail-fast at
/// startup via <see cref="StartupGuards.EnsureUpstreamOidcEndpointsAsync"/> rather than a lazy
/// first-request resolve.
/// </para>
/// </remarks>
public sealed class UpstreamOidcMetadata(
    IHttpClientFactory httpClientFactory,
    IOptions<OAuthOptions> options,
    IMemoryCache cache,
    ILogger<UpstreamOidcMetadata> logger)
{
    /// <summary>Named <see cref="HttpClient"/> used for the discovery fetch, registered in Program.cs.</summary>
    public const string HttpClientName = "upstream-oidc-discovery";

    /// <summary>
    /// <see cref="IMemoryCache"/> key holding the resolved endpoints. Public so a test can expire the
    /// entry deliberately and exercise the refresh path without waiting out <see cref="CacheDuration"/>.
    /// </summary>
    public const string CacheKey = "upstream-oidc:endpoints";

    /// <summary>
    /// How long a resolved document is reused. Discovery documents change on the order of years, so
    /// this is about picking up a genuine provider change without a redeploy, not about freshness.
    /// </summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(12);

    /// <summary>
    /// Last successfully resolved document, kept outside <see cref="IMemoryCache"/> so it survives
    /// the TTL. Serving it when a *refresh* fails is deliberate: startup already proved the provider
    /// reachable and its endpoints good, so a later blip should not take the proxy down with it. The
    /// fail-fast that matters — never serving endpoints we have not verified — happens at startup.
    /// </summary>
    private UpstreamOidcEndpoints? _lastKnownGood;

    /// <summary>
    /// Returns the upstream endpoints, fetching and caching the discovery document on first use.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document could not be fetched, is not JSON, or omits one of the four endpoints — and no
    /// earlier resolution succeeded to fall back on.
    /// </exception>
    public async Task<UpstreamOidcEndpoints> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<UpstreamOidcEndpoints>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var discoveryUrl = DiscoveryUrl(options.Value.Authority);

        UpstreamOidcEndpoints endpoints;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(discoveryUrl, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"OIDC discovery document at '{discoveryUrl}' returned HTTP {(int)response.StatusCode}.");
            }

            endpoints = Parse(body, discoveryUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && _lastKnownGood is not null)
        {
            logger.LogWarning(ex,
                "Failed to refresh the OIDC discovery document from {DiscoveryUrl}; continuing with the last resolved endpoints.",
                discoveryUrl);
            return _lastKnownGood;
        }

        _lastKnownGood = endpoints;
        cache.Set(CacheKey, endpoints, CacheDuration);
        return endpoints;
    }

    /// <summary>
    /// Builds the discovery URL for an issuer. The single concatenation this class permits itself —
    /// the well-known path is standardised, the endpoint paths beneath it are not.
    /// </summary>
    public static string DiscoveryUrl(string? authority) =>
        $"{(authority ?? string.Empty).TrimEnd('/')}/.well-known/openid-configuration";

    /// <summary>
    /// Parses a discovery document into the four endpoints we need, rejecting anything incomplete.
    /// <paramref name="discoveryUrl"/> only appears in error messages, so a failure names the
    /// document that caused it.
    /// </summary>
    public static UpstreamOidcEndpoints Parse(string json, string discoveryUrl)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' is not valid JSON.", ex);
        }

        using (doc)
        {
            return new UpstreamOidcEndpoints(
                RequireEndpoint(doc.RootElement, "authorization_endpoint", discoveryUrl),
                RequireEndpoint(doc.RootElement, "token_endpoint", discoveryUrl),
                RequireEndpoint(doc.RootElement, "jwks_uri", discoveryUrl),
                RequireEndpoint(doc.RootElement, "userinfo_endpoint", discoveryUrl));
        }
    }

    private static string RequireEndpoint(JsonElement root, string name, string discoveryUrl)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' has no string '{name}'. The OAuth proxy " +
                "needs all of authorization_endpoint, token_endpoint, jwks_uri and userinfo_endpoint.");
        }

        var url = value.GetString()!.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' gives '{name}' as '{url}', which is not an absolute https URI.");
        }

        return url;
    }
}
