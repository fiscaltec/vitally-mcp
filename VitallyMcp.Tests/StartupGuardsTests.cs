using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

public sealed class StartupGuardsTests : IDisposable
{
    /// <summary>
    /// Caches handed to resolvers, disposed with the class. <see cref="MemoryCache"/> is
    /// <see cref="IDisposable"/> and CodeQL flags leaving one un-disposed.
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
    public void EnsureSafeAuthConfig_NoAuthWithKeyVault_Throws()
    {
        var act = () => StartupGuards.EnsureSafeAuthConfig(noAuth: true, keyVaultUri: "https://kv.vault.azure.net/");
        act.Should().Throw<InvalidOperationException>().WithMessage("*NoAuth*");
    }

    [Theory]
    [InlineData(false, "https://kv.vault.azure.net/")] // auth on + KV: fine (production)
    [InlineData(true, null)]                            // NoAuth + no KV: fine (local dev)
    [InlineData(true, "")]                              // NoAuth + blank KV: fine
    [InlineData(false, null)]                           // auth on + no KV: fine (dev key)
    public void EnsureSafeAuthConfig_SafeCombinations_DoNotThrow(bool noAuth, string? keyVaultUri)
    {
        var act = () => StartupGuards.EnsureSafeAuthConfig(noAuth, keyVaultUri);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task EnsureUpstreamOidcEndpoints_UnreachableProvider_Throws()
    {
        // Booting anyway would mean publishing jwks_uri and userinfo_endpoint to clients without
        // ever having seen the document they come from. A refusal to start is the lesser failure.
        using var handler = new StubOidcDiscovery.FailingHandler();

        var act = async () => await StartupGuards.EnsureUpstreamOidcEndpointsAsync(
            BuildResolver(handler), proxyEnabled: true, timeout: TimeSpan.FromSeconds(5));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Refusing to start*")
            .And.Message.Should().Contain("OAuth:Authority", "the message has to say what to check");
    }

    [Fact]
    public async Task EnsureUpstreamOidcEndpoints_DocumentMissingAnEndpoint_Throws()
    {
        // Issuer intact so the document passes the issuer check and fails on the missing endpoint —
        // otherwise this would assert the wrong rejection reason.
        using var handler = new StubOidcDiscovery.StubHandler(StubOidcDiscovery.BuildDocument(omit: "userinfo_endpoint"));

        var act = async () => await StartupGuards.EnsureUpstreamOidcEndpointsAsync(
            BuildResolver(handler), proxyEnabled: true, timeout: TimeSpan.FromSeconds(5));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*userinfo_endpoint*");
    }

    [Fact]
    public async Task EnsureUpstreamOidcEndpoints_ProxyDisabled_DoesNotEvenAsk()
    {
        // Without OAuth:SharedClientId there are no proxy endpoints, so nothing reads the document —
        // and a deployment that never uses it must not be made to depend on the provider being up.
        using var handler = new StubOidcDiscovery.FailingHandler();

        var act = async () => await StartupGuards.EnsureUpstreamOidcEndpointsAsync(
            BuildResolver(handler), proxyEnabled: false, timeout: TimeSpan.FromSeconds(5));

        await act.Should().NotThrowAsync();
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsureUpstreamOidcEndpoints_UsableDocument_ResolvesAndWarmsTheCache()
    {
        // The guard doubles as the warm-up: the first real request must not pay for the fetch.
        using var handler = new StubOidcDiscovery.StubHandler(StubOidcDiscovery.Document);
        var resolver = BuildResolver(handler);

        await StartupGuards.EnsureUpstreamOidcEndpointsAsync(resolver, proxyEnabled: true, timeout: TimeSpan.FromSeconds(5));
        var endpoints = await resolver.GetAsync();

        handler.CallCount.Should().Be(1);
        endpoints.TokenEndpoint.Should().Be(StubOidcDiscovery.TokenEndpoint);
    }

    private UpstreamOidcMetadata BuildResolver(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(UpstreamOidcMetadata.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var cache = new MemoryCache(new MemoryCacheOptions());
        _caches.Add(cache);

        return new UpstreamOidcMetadata(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            // Must equal the stub document's issuer: the resolver checks the two against each other.
            Options.Create(new OAuthOptions { Authority = StubOidcDiscovery.Issuer }),
            cache,
            NullLogger<UpstreamOidcMetadata>.Instance);
    }
}
