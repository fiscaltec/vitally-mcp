using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

/// <summary>
/// The OAuth proxy used to build its four upstream URLs by concatenating provider-specific path
/// shapes onto <c>OAuth:Authority</c>, which only ever produced Auth0's. These tests pin the
/// replacement: the values come from the provider's own discovery document, are reused rather than
/// refetched, and an unusable document is a loud failure rather than a plausible-looking wrong URL.
/// </summary>
public class UpstreamOidcMetadataTests
{
    private const string Authority = "https://example-idp.com/tenant-id/v2.0";

    [Fact]
    public async Task GetAsync_ReadsAllFourEndpointsFromTheDiscoveryDocument()
    {
        // The stub's endpoints are Entra-shaped while Authority is not, so none of these four is
        // reachable by concatenation — passing means the document drove every one of them.
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
            .Which.Should().Be($"{Authority}/.well-known/openid-configuration");
    }

    [Theory]
    [InlineData("https://example-idp.com/tenant-id/v2.0/")]
    [InlineData("https://example-idp.com/tenant-id/v2.0")]
    public void DiscoveryUrl_DoesNotDoubleTheSlashOnATrailingSlashAuthority(string authority)
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
    public async Task GetAsync_ThrowsWhenTheProviderIsUnreachable()
    {
        using var handler = new StubOidcDiscovery.FailingHandler();
        var (resolver, _) = BuildResolver(handler);

        var act = async () => await resolver.GetAsync();

        await act.Should().ThrowAsync<HttpRequestException>();
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

        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.GetAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => resolver.GetAsync());

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_KeepsServingTheLastResolvedEndpointsWhenARefreshFails()
    {
        // Startup already proved these endpoints good (StartupGuards refuses to boot otherwise), so
        // a later provider blip should degrade to what we verified rather than 500 the proxy. The
        // fail-fast that matters happens before the server accepts traffic, not here.
        using var handler = new StubOidcDiscovery.ThenFailingHandler(StubOidcDiscovery.Document);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var (resolver, _) = BuildResolver(handler, cache);

        var first = await resolver.GetAsync();
        // Simulate the TTL elapsing — the next call must go back to the wire, and that call fails.
        cache.Remove(UpstreamOidcMetadata.CacheKey);
        var afterFailedRefresh = await resolver.GetAsync();

        handler.CallCount.Should().Be(2, "the refresh was genuinely attempted, not skipped");
        afterFailedRefresh.Should().BeEquivalentTo(first);
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
        var document = RemoveField(StubOidcDiscovery.Document, missingField);

        var act = () => UpstreamOidcMetadata.Parse(document, "https://idp.example/.well-known/openid-configuration");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{missingField}*")
            .And.Message.Should().Contain("https://idp.example/.well-known/openid-configuration",
                "the error has to name the document that caused it");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"authorization_endpoint\": ")]
    public void Parse_RejectsAMalformedDocument(string body)
    {
        var act = () => UpstreamOidcMetadata.Parse(body, "https://idp.example/.well-known/openid-configuration");

        act.Should().Throw<InvalidOperationException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Parse_RejectsADocumentThatIsNotAJsonObject()
    {
        var act = () => UpstreamOidcMetadata.Parse("[]", "https://idp.example/.well-known/openid-configuration");

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
          "authorization_endpoint": {{rawValue}},
          "token_endpoint": "{{StubOidcDiscovery.TokenEndpoint}}",
          "jwks_uri": "{{StubOidcDiscovery.JwksUri}}",
          "userinfo_endpoint": "{{StubOidcDiscovery.UserInfoEndpoint}}"
        }
        """;

        var act = () => UpstreamOidcMetadata.Parse(document, "https://idp.example/.well-known/openid-configuration");

        act.Should().Throw<InvalidOperationException>().WithMessage("*authorization_endpoint*");
    }

    /// <summary>
    /// Builds a resolver over a stub handler. Returns the cache too so a test can share it between
    /// instances or expire it by hand.
    /// </summary>
    private static (UpstreamOidcMetadata Resolver, IMemoryCache Cache) BuildResolver(
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

        cache ??= new MemoryCache(new MemoryCacheOptions());
        var resolver = new UpstreamOidcMetadata(
            factory,
            Options.Create(new OAuthOptions { Authority = Authority }),
            cache,
            NullLogger<UpstreamOidcMetadata>.Instance);
        return (resolver, cache);
    }

    /// <summary>Removes one top-level property from a JSON document, for the missing-field cases.</summary>
    private static string RemoveField(string json, string field)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals(field)) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
