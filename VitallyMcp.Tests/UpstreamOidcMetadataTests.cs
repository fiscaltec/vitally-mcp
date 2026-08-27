using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

/// <summary>
/// The OAuth proxy used to build its four upstream URLs by concatenating provider-specific path
/// shapes onto <c>OAuth:Authority</c>, which only ever produced Auth0's. These tests pin the
/// replacement: the values come from the provider's own discovery document, that document has to
/// speak for the configured issuer, they are reused rather than refetched, and an unusable document
/// is a loud failure rather than a plausible-looking wrong URL.
/// </summary>
public sealed class UpstreamOidcMetadataTests : IDisposable
{
    private const string DiscoveryUrl = "https://idp.example/.well-known/openid-configuration";

    /// <summary>
    /// Caches handed to resolvers, disposed with the class. <see cref="MemoryCache"/> is
    /// <see cref="IDisposable"/> and CodeQL flags leaving one un-disposed, so they are tracked rather
    /// than dropped on the floor.
    /// </summary>
    private readonly List<MemoryCache> _caches = [];

    public void Dispose()
    {
        foreach (var cache in _caches)
        {
            cache.Dispose();
        }
    }

    [Fact]
    public async Task GetAsync_ReadsAllFourEndpointsFromTheDiscoveryDocument()
    {
        // The stub's endpoints are on hosts and paths unrelated to the issuer, so none of these four
        // is reachable by concatenation — passing means the document drove every one of them.
        using var handler = new StubOidcDiscovery.StubHandler(StubOidcDiscovery.Document);
        var (resolver, _) = BuildResolver(handler);

        var endpoints = await resolver.GetAsync();

        endpoints.AuthorizationEndpoint.Should().Be(StubOidcDiscovery.AuthorizationEndpoint);
        endpoints.TokenEndpoint.Should().Be(StubOidcDiscovery.TokenEndpoint);
        endpoints.JwksUri.Should().Be(StubOidcDiscovery.JwksUri);
        endpoints.UserInfoEndpoint.Should().Be(StubOidcDiscovery.UserInfoEndpoint,
            "userinfo lives on a different host entirely — the case no Authority value can cover");
    }

    [Fact]
    public async Task GetAsync_FetchesTheStandardWellKnownPathBeneathTheAuthority()
    {
        // The discovery path is the one concatenation that is safe: it is standardised by OIDC
        // Discovery §4, unlike the endpoint paths beneath it.
        using var handler = new StubOidcDiscovery.StubHandler(StubOidcDiscovery.Document);
        var (resolver, _) = BuildResolver(handler);

        await resolver.GetAsync();

        handler.RequestedUrls.Should().ContainSingle()
            .Which.Should().Be("https://example.auth0.com/.well-known/openid-configuration");
    }

    [Theory]
    [InlineData("https://example-idp.com/tenant-id/v2.0/")]
    [InlineData("https://example-idp.com/tenant-id/v2.0")]
    public void DiscoveryUrlFor_DoesNotDoubleTheSlashOnATrailingSlashAuthority(string authority)
    {
        // Auth0 issuers conventionally carry a trailing slash and Entra's do not, so both shapes
        // reach this code in practice.
        UpstreamOidcMetadata.DiscoveryUrl(authority)
            .Should().Be("https://example-idp.com/tenant-id/v2.0/.well-known/openid-configuration");
    }

    [Fact]
    public async Task GetAsync_ReusesTheCachedDocumentInsteadOfRefetching()
    {
        // Every /oauth/authorize, /oauth/token and metadata request reads these endpoints. Without
        // the cache the proxy would put a round-trip to the identity provider in front of each one.
        using var handler = new StubOidcDiscovery.StubHandler(StubOidcDiscovery.Document);
        var (resolver, _) = BuildResolver(handler);

        var first = await resolver.GetAsync();
        var second = await resolver.GetAsync();

        handler.CallCount.Should().Be(1);
        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task GetAsync_SharesTheCacheAcrossResolverInstances()
    {
        // The cache lives in IMemoryCache rather than in the instance, so a second resolver over the
        // same container (or a rebuilt singleton) does not re-hit the provider.
        using var handler = new StubOidcDiscovery.StubHandler(StubOidcDiscovery.Document);
        var (first, cache) = BuildResolver(handler);
        var (second, _) = BuildResolver(handler, cache);

        await first.GetAsync();
        await second.GetAsync();

        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_SurfacesAnUnreachableProviderAsAnInvalidOperation()
    {
        // Transport failures are converted rather than propagated so callers — StartupGuards above
        // all — have one exception type to catch instead of a catch-all.
        using var handler = new StubOidcDiscovery.FailingHandler();
        var (resolver, _) = BuildResolver(handler);

        var act = async () => await resolver.GetAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithInnerException<HttpRequestException>();
    }

    [Fact]
    public async Task GetAsync_ThrowsWhenDiscoveryAnswersNonSuccess()
    {
        using var handler = new StubOidcDiscovery.StubHandler("not found", System.Net.HttpStatusCode.NotFound);
        var (resolver, _) = BuildResolver(handler);

        var act = async () => await resolver.GetAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*404*");
    }

    [Fact]
    public async Task GetAsync_DoesNotCacheAFailedResolve()
    {
        // A transient failure must not be remembered as an answer — the next caller has to retry.
        using var handler = new StubOidcDiscovery.FailingHandler();
        var (resolver, _) = BuildResolver(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.GetAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.GetAsync());

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_KeepsServingTheLastResolvedEndpointsWhenARefreshFails()
    {
        // Startup already proved these endpoints good (StartupGuards refuses to boot otherwise), so
        // a later provider blip should degrade to what we verified rather than 500 the proxy. The
        // fail-fast that matters happens before the server accepts traffic, not here.
        using var handler = new StubOidcDiscovery.ThenFailingHandler(StubOidcDiscovery.Document);
        var (resolver, cache) = BuildResolver(handler);

        var first = await resolver.GetAsync();
        // Simulate the TTL elapsing — the next call must go back to the wire, and that call fails.
        cache.Remove(UpstreamOidcMetadata.CacheKey);
        var afterFailedRefresh = await resolver.GetAsync();

        handler.CallCount.Should().Be(2, "the refresh was genuinely attempted, not skipped");
        afterFailedRefresh.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task GetAsync_FallsBackWhenTheRefreshTimesOutRatherThanFailingTheRequest()
    {
        // An HttpClient timeout surfaces as TaskCanceledException, which is an
        // OperationCanceledException. Filtering the fallback on the exception *type* would therefore
        // have excluded a slow provider — precisely the case the fallback exists for. The filter
        // keys on the caller's token instead, and this pins that distinction.
        using var handler = new StubOidcDiscovery.ThenFailingHandler(
            StubOidcDiscovery.Document, () => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var (resolver, cache) = BuildResolver(handler);

        var first = await resolver.GetAsync();
        cache.Remove(UpstreamOidcMetadata.CacheKey);
        var afterTimeout = await resolver.GetAsync();

        afterTimeout.Should().BeEquivalentTo(first);
    }

    [Fact]
    public async Task GetAsync_PropagatesCancellationRatherThanServingStaleEndpoints()
    {
        // The one case where the fallback must not apply: the caller has gone away, so there is no
        // one left to serve, and swallowing their cancellation would hide it.
        using var handler = new StubOidcDiscovery.ThenFailingHandler(
            StubOidcDiscovery.Document, () => new TaskCanceledException("cancelled"));
        var (resolver, cache) = BuildResolver(handler);

        await resolver.GetAsync();
        cache.Remove(UpstreamOidcMetadata.CacheKey);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = async () => await resolver.GetAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAsync_ReCachesTheStaleCopySoAnOutageIsNotRetriedPerRequest()
    {
        // Without re-caching, every request during a prolonged provider outage would attempt its own
        // discovery fetch and wait up to the client timeout — turning the fallback from an absorber
        // of the outage into an amplifier of it.
        using var handler = new StubOidcDiscovery.ThenFailingHandler(StubOidcDiscovery.Document);
        var (resolver, cache) = BuildResolver(handler);

        await resolver.GetAsync();
        cache.Remove(UpstreamOidcMetadata.CacheKey);
        await resolver.GetAsync();           // refresh fails, stale copy re-cached
        await resolver.GetAsync();           // must be served from cache
        await resolver.GetAsync();

        handler.CallCount.Should().Be(2, "only the initial resolve and the one failed refresh reach the wire");
    }

    [Theory]
    [InlineData("https://attacker.example.com/")]
    [InlineData("https://example.auth0.com.evil.test/")]
    [InlineData("https://example.auth0.com/tenant")]
    public void Parse_RejectsADocumentSpeakingForADifferentIssuer(string declaredIssuer)
    {
        // OIDC Discovery §4.3 — the same anti-mix-up control as RFC 8414 §3.3. The discovery client
        // follows redirects, so without this a redirect could substitute another provider's
        // endpoints, which we would cache and then republish to clients as this provider's.
        var document = StubOidcDiscovery.BuildDocument(issuer: declaredIssuer);

        var act = () => UpstreamOidcMetadata.Parse(document, DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>().WithMessage("*does not match OAuth:Authority*");
    }

    [Theory]
    [InlineData("https://example.auth0.com/", "https://example.auth0.com")]
    [InlineData("https://example.auth0.com", "https://example.auth0.com/")]
    public void Parse_ToleratesATrailingSlashDifferenceOnTheIssuer(string declaredIssuer, string configuredAuthority)
    {
        // Auth0 issuers conventionally carry the slash and Entra's do not, so configuration drifts by
        // exactly one character in practice. Tolerating that much — and nothing else — absorbs the
        // drift without weakening the check.
        var document = StubOidcDiscovery.BuildDocument(issuer: declaredIssuer);

        var act = () => UpstreamOidcMetadata.Parse(document, DiscoveryUrl, configuredAuthority);

        act.Should().NotThrow();
    }

    [Fact]
    public void Parse_RejectsADocumentWithNoIssuerAtAll()
    {
        var document = StubOidcDiscovery.BuildDocument(omit: "issuer");

        var act = () => UpstreamOidcMetadata.Parse(document, DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>().WithMessage("*no string 'issuer'*");
    }

    [Theory]
    [InlineData("authorization_endpoint")]
    [InlineData("token_endpoint")]
    [InlineData("jwks_uri")]
    [InlineData("userinfo_endpoint")]
    public void Parse_RejectsADocumentMissingAnyRequiredEndpoint(string missingField)
    {
        // All four are load-bearing: two are called on the wire, two are republished to clients as
        // fact in our RFC 8414 document. Silently omitting one is worse than refusing the document.
        var document = StubOidcDiscovery.BuildDocument(omit: missingField);

        var act = () => UpstreamOidcMetadata.Parse(document, DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{missingField}*")
            .And.Message.Should().Contain(DiscoveryUrl, "the error has to name the document that caused it");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"authorization_endpoint\": ")]
    public void Parse_RejectsAMalformedDocument(string body)
    {
        var act = () => UpstreamOidcMetadata.Parse(body, DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Parse_RejectsADocumentThatIsNotAJsonObject()
    {
        var act = () => UpstreamOidcMetadata.Parse("[]", DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("1234")]
    [InlineData("\"/oauth2/v2.0/authorize\"")]
    [InlineData("\"http://login.example-idp.com/authorize\"")]
    public void Parse_RejectsAnEndpointThatIsNotAnAbsoluteHttpsUri(string rawValue)
    {
        // A relative path or a plaintext http endpoint would be republished to clients verbatim, so
        // it has to be refused here rather than passed through as if the provider knew best.
        var document = $$"""
        {
          "issuer": "{{StubOidcDiscovery.Issuer}}",
          "authorization_endpoint": {{rawValue}},
          "token_endpoint": "{{StubOidcDiscovery.TokenEndpoint}}",
          "jwks_uri": "{{StubOidcDiscovery.JwksUri}}",
          "userinfo_endpoint": "{{StubOidcDiscovery.UserInfoEndpoint}}"
        }
        """;

        var act = () => UpstreamOidcMetadata.Parse(document, DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>().WithMessage("*authorization_endpoint*");
    }

    [Theory]
    [InlineData("authorization_endpoint")]
    [InlineData("token_endpoint")]
    [InlineData("jwks_uri")]
    [InlineData("userinfo_endpoint")]
    public void Parse_RejectsAnEndpointCarryingAUriFragment(string endpointName)
    {
        // An absolute https URI can still carry a fragment, and the consequence is silent rather than
        // loud: /oauth/authorize appends `?response_type=…` to the authorization endpoint, and
        // everything after a '#' is fragment — so the whole query, callback included, would be
        // trapped client-side and never reach the provider. RFC 6749 §3.1 forbids it, and
        // OAuthOptions.IsRedirectUriAllowed already rejects fragments at the other end of this flow.
        var document = StubOidcDiscovery.BuildDocument(
            overrideName: endpointName,
            overrideValue: "https://login.example-idp.com/endpoint#fragment");

        var act = () => UpstreamOidcMetadata.Parse(document, DiscoveryUrl, StubOidcDiscovery.Issuer);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{endpointName}*")
            .And.Message.Should().Contain("fragment");
    }

    /// <summary>
    /// Builds a resolver over a stub handler. Returns the cache too so a test can share it between
    /// instances or expire it by hand.
    /// </summary>
    private (UpstreamOidcMetadata Resolver, IMemoryCache Cache) BuildResolver(
        HttpMessageHandler handler,
        IMemoryCache? cache = null)
    {
        var services = new ServiceCollection();
        // The same instance every time, so a test that shares one handler across two resolvers sees
        // a single call count. The factory's handler lifetime (2 minutes) far outlives any test, so
        // it never recycles and disposes it mid-run; the test's `using` owns disposal.
        services.AddHttpClient(UpstreamOidcMetadata.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        if (cache is null)
        {
            var owned = new MemoryCache(new MemoryCacheOptions());
            _caches.Add(owned);
            cache = owned;
        }

        var resolver = new UpstreamOidcMetadata(
            factory,
            Options.Create(new OAuthOptions { Authority = StubOidcDiscovery.Issuer }),
            cache,
            NullLogger<UpstreamOidcMetadata>.Instance);
        return (resolver, cache);
    }
}
