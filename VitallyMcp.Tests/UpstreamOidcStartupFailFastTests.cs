using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// The fail-fast in <see cref="StartupGuards.EnsureUpstreamOidcEndpointsAsync"/> is only worth
/// anything if it is actually wired into the composition root, and that wiring is the easy half to
/// lose: the guard sits between <c>builder.Build()</c> and <c>app.Run()</c>, where a refactor can
/// silently drop it while every other test still passes. These drive the real Program.cs.
/// </summary>
public class UpstreamOidcStartupFailFastTests
{
    [Fact]
    public void Host_RefusesToStartWhenTheProviderDiscoveryDocumentIsUnreachable()
    {
        // Booting regardless would put unverified jwks_uri and userinfo_endpoint values into the
        // RFC 8414 document every MCP client reads.
        using var factory = new Factory(proxyEnabled: true, discovery: null);

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("Refusing to start");
    }

    [Fact]
    public void Host_RefusesToStartWhenTheDiscoveryDocumentOmitsAnEndpointTheProxyNeeds()
    {
        const string missingUserInfo = """
        {
          "issuer": "https://login.example-idp.com/tenant-id/v2.0",
          "authorization_endpoint": "https://login.example-idp.com/tenant-id/oauth2/v2.0/authorize",
          "token_endpoint": "https://login.example-idp.com/tenant-id/oauth2/v2.0/token",
          "jwks_uri": "https://login.example-idp.com/tenant-id/discovery/v2.0/keys"
        }
        """;
        using var factory = new Factory(proxyEnabled: true, discovery: missingUserInfo);

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>()
            .Which.ToString().Should().Contain("userinfo_endpoint");
    }

    [Fact]
    public async Task Host_StartsWithoutDiscoveryWhenTheOAuthProxyIsDisabled()
    {
        // No OAuth:SharedClientId means no /oauth/* endpoints and no RFC 8414 document, so nothing
        // reads the discovery document — and such a deployment must not be made to depend on the
        // provider being reachable at boot.
        using var factory = new Factory(proxyEnabled: false, discovery: null);

        using var client = factory.CreateClient();
        var health = await client.GetAsync("/health");

        health.IsSuccessStatusCode.Should().BeTrue();
    }

    /// <param name="proxyEnabled">Whether to set <c>OAuth:SharedClientId</c>.</param>
    /// <param name="discovery">Document to serve, or <c>null</c> to make every fetch fail.</param>
    private sealed class Factory(bool proxyEnabled, string? discovery) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["OAuth:NoAuth"] = "true",
                    ["OAuth:Authority"] = "https://example.auth0.com/",
                    ["OAuth:Audience"] = "https://vitally.example.com",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                };
                if (proxyEnabled)
                {
                    settings["OAuth:SharedClientId"] = "test-client-id";
                }
                config.AddInMemoryCollection(settings);
            });
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient(UpstreamOidcMetadata.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => discovery is null
                        ? new StubOidcDiscovery.FailingHandler()
                        : new StubOidcDiscovery.StubHandler(discovery));
            });
            return base.CreateHost(builder);
        }
    }
}
