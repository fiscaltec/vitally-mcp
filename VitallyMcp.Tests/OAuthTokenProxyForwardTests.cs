using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

/// <summary>
/// Where <c>/oauth/token</c> actually forwards to. The sibling proxy tests only reach the
/// unsupported-grant guard, which returns before any upstream call, and the resolver tests only
/// assert the record it returns — so reverting the forward to the old <c>{authority}/oauth/token</c>
/// would leave the whole suite green. This class closes that hole by stubbing the *default*
/// <see cref="HttpClient"/> the handler resolves from <see cref="IHttpClientFactory"/> and reading
/// back the URI it posted to.
/// </summary>
public class OAuthTokenProxyForwardTests : IClassFixture<OAuthTokenProxyForwardTests.Factory>
{
    private readonly Factory _factory;

    public OAuthTokenProxyForwardTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Token_ForwardsToTheDiscoveredTokenEndpoint()
    {
        using var client = _factory.CreateClient();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test-code",
            ["client_id"] = "test-client-id",
            ["redirect_uri"] = "http://localhost:54321/callback",
            ["code_verifier"] = "test-verifier"
        });
        var response = await client.PostAsync("/oauth/token", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("test-access-token",
            "the upstream body is passed through to the caller unchanged");

        // Asserted across every request the fixture has seen, not as a single item: the factory is a
        // class fixture, so the sibling test's forward lands in the same list and xUnit does not fix
        // the order the two methods run in. `ContainSingle` passed locally and failed on CI purely on
        // that ordering.
        _factory.Upstream.RequestedUrls.Should().NotBeEmpty()
            .And.AllBeEquivalentTo(StubOidcDiscovery.TokenEndpoint,
                "the token endpoint comes from the discovery document, not from OAuth:Authority");
    }

    [Fact]
    public async Task Token_ReplacesTheClientRedirectUriWithOurOwnCallback()
    {
        // The upstream app only ever knows our fixed callback, so the code exchange has to present
        // that same value — not the loopback URI the client used. Asserted here because this is the
        // only test that sees the forwarded body at all.
        using var client = _factory.CreateClient();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test-code",
            ["redirect_uri"] = "http://localhost:54321/callback"
        });
        await client.PostAsync("/oauth/token", form);

        // Tests within a class share the fixture and run sequentially, so the last recorded body is
        // this test's own.
        var body = _factory.Upstream.RequestBodies.Last();
        body.Should().Contain(Uri.EscapeDataString("http://localhost/oauth/callback"));
        body.Should().NotContain(Uri.EscapeDataString("http://localhost:54321/callback"));
    }

    public class Factory : WebApplicationFactory<Program>
    {
        /// <summary>Stands in for the upstream token endpoint; records what the proxy sent it.</summary>
        public StubOidcDiscovery.CapturingHandler Upstream { get; } =
            new("""{"access_token":"test-access-token","token_type":"Bearer","expires_in":3600}""");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OAuth:NoAuth"] = "true",
                    ["OAuth:Authority"] = StubOidcDiscovery.Issuer,
                    ["OAuth:Audience"] = "https://vitally.example.com",
                    ["OAuth:SharedClientId"] = "test-client-id",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.UseStubDiscovery();
                // /oauth/token forwards via factory.CreateClient() — the *default* (unnamed) client.
                // Configuring Options.DefaultName reaches exactly that one; the typed clients
                // (VitallyService, GraphGroupPermissionResolver) and the named discovery client are
                // registered under their own names and are unaffected.
                services.AddHttpClient(Options.DefaultName)
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
            return base.CreateHost(builder);
        }
    }
}
