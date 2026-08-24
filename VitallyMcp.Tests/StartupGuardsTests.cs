using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class StartupGuardsTests
{
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
        const string incomplete = """
        { "issuer": "https://idp.example/v2.0", "authorization_endpoint": "https://idp.example/authorize" }
        """;
        using var handler = new StubOidcDiscovery.StubHandler(incomplete);

        var act = async () => await StartupGuards.EnsureUpstreamOidcEndpointsAsync(
            BuildResolver(handler), proxyEnabled: true, timeout: TimeSpan.FromSeconds(5));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithInnerException<InvalidOperationException>()
            .WithMessage("*token_endpoint*");
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

    private static UpstreamOidcMetadata BuildResolver(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(UpstreamOidcMetadata.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return new UpstreamOidcMetadata(
            services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(),
            Options.Create(new OAuthOptions { Authority = "https://idp.example/tenant/v2.0" }),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<UpstreamOidcMetadata>.Instance);
    }
}
