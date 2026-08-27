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
/// <para>
/// Every failure mode surfaces as <see cref="InvalidOperationException"/> — transport errors
/// included — so callers have one type to catch rather than a catch-all.
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
    /// How long the last-known-good copy is re-cached after a failed refresh. Without this, a
    /// prolonged provider outage would put a fresh discovery attempt — and a wait of up to the
    /// client timeout — in front of *every* proxy request once the TTL lapsed, turning a fallback
    /// meant to absorb the outage into an amplifier of it. Short enough that recovery is picked up
    /// promptly, long enough that requests are served from memory meanwhile.
    /// </summary>
    public static readonly TimeSpan FailedRefreshRetryInterval = TimeSpan.FromMinutes(1);

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
    /// The document could not be fetched, is not JSON, speaks for a different issuer, or omits one of
    /// the four endpoints — and no earlier resolution succeeded to fall back on.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
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
            endpoints = await FetchAsync(discoveryUrl, cancellationToken);
        }
        // Keyed on the caller's token rather than on the exception type. An HttpClient timeout
        // surfaces as TaskCanceledException — an OperationCanceledException — so filtering the
        // *type* out would have sent exactly the failure this fallback exists for straight to the
        // caller. Only a genuinely cancelled caller skips the fallback, because there is then no
        // one left to serve.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && _lastKnownGood is not null)
        {
            logger.LogWarning(ex,
                "Failed to refresh the OIDC discovery document from {DiscoveryUrl}; continuing with the last resolved endpoints for {RetryInterval}.",
                discoveryUrl, FailedRefreshRetryInterval);
            cache.Set(CacheKey, _lastKnownGood, FailedRefreshRetryInterval);
            return _lastKnownGood;
        }

        _lastKnownGood = endpoints;
        cache.Set(CacheKey, endpoints, CacheDuration);
        return endpoints;
    }

    private async Task<UpstreamOidcEndpoints> FetchAsync(string discoveryUrl, CancellationToken cancellationToken)
    {
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

            return Parse(body, discoveryUrl, options.Value.Authority);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' could not be fetched: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the discovery URL for an issuer. The single concatenation this class permits itself —
    /// the well-known path is standardised, the endpoint paths beneath it are not.
    /// </summary>
    public static string DiscoveryUrl(string? authority) =>
        $"{(authority ?? string.Empty).TrimEnd('/')}/.well-known/openid-configuration";

    /// <summary>
    /// Parses a discovery document into the four endpoints we need, rejecting anything incomplete or
    /// speaking for an issuer other than <paramref name="expectedIssuer"/>.
    /// <paramref name="discoveryUrl"/> only appears in error messages, so a failure names the
    /// document that caused it.
    /// </summary>
    /// <param name="json">Raw document body.</param>
    /// <param name="discoveryUrl">Where it came from, for diagnostics.</param>
    /// <param name="expectedIssuer">The configured <see cref="OAuthOptions.Authority"/>.</param>
    public static UpstreamOidcEndpoints Parse(string json, string discoveryUrl, string? expectedIssuer)
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
            RequireMatchingIssuer(doc.RootElement, discoveryUrl, expectedIssuer);

            return new UpstreamOidcEndpoints(
                RequireEndpoint(doc.RootElement, "authorization_endpoint", discoveryUrl),
                RequireEndpoint(doc.RootElement, "token_endpoint", discoveryUrl),
                RequireEndpoint(doc.RootElement, "jwks_uri", discoveryUrl),
                RequireEndpoint(doc.RootElement, "userinfo_endpoint", discoveryUrl));
        }
    }

    /// <summary>
    /// OIDC Discovery §4.3: the <c>issuer</c> in the document must match the issuer the document was
    /// requested for. It is the same anti-mix-up control as RFC 8414 §3.3 — a metadata document can
    /// only ever speak for itself — and it is what stops a redirect (the discovery client follows
    /// them) from substituting another provider's endpoints, which we would then cache and
    /// republish to clients as this provider's.
    /// </summary>
    private static void RequireMatchingIssuer(JsonElement root, string discoveryUrl, string? expectedIssuer)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("issuer", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' has no string 'issuer', so it cannot be " +
                "checked against OAuth:Authority. Refusing to trust its endpoints.");
        }

        // Trailing slashes are normalised on both sides and nothing else is: the comparison stays a
        // literal string match per the spec. Auth0 issuers conventionally carry the slash and
        // Entra's do not, so tolerating exactly that much absorbs configuration drift without
        // weakening the control.
        var published = value.GetString()!.Trim().TrimEnd('/');
        var expected = (expectedIssuer ?? string.Empty).Trim().TrimEnd('/');

        if (!string.Equals(published, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' declares issuer '{value.GetString()}', which " +
                $"does not match OAuth:Authority ('{expectedIssuer}'). A metadata document can only speak " +
                "for its own issuer (OIDC Discovery §4.3), so this one is refused rather than trusted.");
        }
    }

    private static string RequireEndpoint(JsonElement root, string name, string discoveryUrl)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
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

        // RFC 6749 §3.1/§3.2 — an endpoint URI must not carry a fragment. Silently destructive rather
        // than merely invalid: /oauth/authorize appends `?response_type=…` to the authorization
        // endpoint, and appending after a fragment traps the whole query — the callback included —
        // on the client side of the '#', so none of it ever reaches the provider. On the other two,
        // the fragment would be republished to clients as part of an unusable endpoint.
        // OAuthOptions.IsRedirectUriAllowed rejects fragments at the other end of the same flow for
        // the same reason.
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                $"OIDC discovery document at '{discoveryUrl}' gives '{name}' as '{url}', which contains a " +
                "URI fragment. OAuth endpoint URIs must not (RFC 6749 §3.1).");
        }

        return url;
    }
}
