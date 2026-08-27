using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// The production shape, which <see cref="OAuthProxyEndpointsTests"/> cannot cover: there
/// <c>OAuth:PublicBaseUrl</c> is set, so the canonical origin is <em>not</em> the request
/// <c>Host</c>. Every identity we publish — the protected-resource document's
/// <c>authorization_servers</c>, the authorization-server document's <c>issuer</c>, and the
/// <c>iss</c> injected on the callback — must be that one configured origin. A regression that
/// silently fell back to the request host would still pass the sibling class's assertions, because
/// there the two values coincide.
/// </summary>
public class OAuthProxyPublicOriginTests : IClassFixture<OAuthProxyPublicOriginTests.Factory>
{
    private const string PublicOrigin = "https://vitally.example.com";

    private readonly Factory _factory;

    public OAuthProxyPublicOriginTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task AuthorizationServerMetadata_DeclaresThePublicBaseUrlAsIssuer()
    {
        using var client = _factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        doc.RootElement.GetProperty("issuer").GetString().Should().Be(PublicOrigin,
            "a spoofed Host header must never be able to steer the identity we publish");
    }

    [Fact]
    public async Task AuthorizationServerMetadata_IssuerMatchesTheAuthorizationServerAdvertisedToClients()
    {
        using var client = _factory.CreateClient();

        using var resourceDoc = JsonDocument.Parse(
            await client.GetStringAsync("/.well-known/oauth-protected-resource"));
        var advertisedAuthorizationServer = resourceDoc.RootElement
            .GetProperty("authorization_servers").EnumerateArray().Single().GetString();

        using var asDoc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        advertisedAuthorizationServer.Should().Be(PublicOrigin);
        asDoc.RootElement.GetProperty("issuer").GetString().Should().Be(advertisedAuthorizationServer);
    }

    [Fact]
    public async Task Callback_InjectsIssMatchingThePublicBaseUrl()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        const string clientRedirectUri = "http://localhost:54321/callback";
        const string state = "public-origin";

        var authorize = await client.GetAsync(
            $"/oauth/authorize?response_type=code&client_id=test&state={state}&redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var callback = await client.GetAsync(
            $"/oauth/callback?code=test-code&state={state}&iss={Uri.EscapeDataString("https://example.auth0.com/")}");
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var iss = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["iss"];
        iss.Count.Should().Be(1);
        iss[0].Should().Be(PublicOrigin);
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OAuth:NoAuth"] = "true",
                    // Must equal StubOidcDiscovery.Issuer — see the sibling factory.
                    ["OAuth:Authority"] = StubOidcDiscovery.Issuer,
                    ["OAuth:Audience"] = PublicOrigin,
                    ["OAuth:PublicBaseUrl"] = PublicOrigin,
                    ["OAuth:SharedClientId"] = "test-client-id",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                });
            });
            // See the sibling factory: startup resolves the upstream endpoints from discovery.
            builder.ConfigureServices(services => services.UseStubDiscovery());
            return base.CreateHost(builder);
        }
    }
}
