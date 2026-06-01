using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// Integration tests for the OAuth proxy endpoints in Program.cs. Uses
/// <see cref="WebApplicationFactory{TEntryPoint}"/> against the real composition root, with
/// configuration overrides supplied via in-memory config so we don't depend on a real Auth0
/// tenant or Key Vault.
/// </summary>
public class OAuthProxyEndpointsTests : IClassFixture<OAuthProxyEndpointsTests.Factory>
{
    private readonly Factory _factory;

    public OAuthProxyEndpointsTests(Factory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://attacker.local/callback")]
    [InlineData("https://claude.ai/api/mcp/auth_callback.evil.com")]
    public async Task Authorize_RejectsDisallowedRedirectUri(string redirectUri)
    {
        // Regression test for the open-redirector finding. Without this validation, the
        // /oauth/callback handler would happily redirect victims to any attacker-controlled
        // URL with the authorisation code in the query string, since the upstream Auth0 app
        // only ever sees our fixed /oauth/callback as redirect_uri.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var query = $"?response_type=code&client_id=test&state=abc123&redirect_uri={Uri.EscapeDataString(redirectUri)}";
        var response = await client.GetAsync("/oauth/authorize" + query);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("redirect_uri is not allowed");
    }

    [Theory]
    [InlineData("http://localhost:54321/callback")]
    [InlineData("http://127.0.0.1:8080/cb")]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    public async Task Authorize_AcceptsAllowedRedirectUri(string redirectUri)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var query = $"?response_type=code&client_id=test&state=abc123&redirect_uri={Uri.EscapeDataString(redirectUri)}";
        var response = await client.GetAsync("/oauth/authorize" + query);

        // Accepted requests are 302-redirected to the upstream Auth0 /authorize.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().StartWith("https://example.auth0.com/authorize?");
    }

    [Fact]
    public async Task Register_RejectsWhenAllRedirectUrisDisallowed()
    {
        using var client = _factory.CreateClient();

        var payload = new
        {
            client_name = "Test Client",
            redirect_uris = new[] { "https://evil.example.com/cb" }
        };
        var response = await client.PostAsJsonAsync("/oauth/register", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("invalid_redirect_uri");
    }

    [Fact]
    public async Task Register_FiltersToOnlyAllowedRedirectUris()
    {
        using var client = _factory.CreateClient();

        var payload = new
        {
            client_name = "Test Client",
            redirect_uris = new[]
            {
                "https://evil.example.com/cb",
                "http://localhost:51234/callback"
            }
        };
        var response = await client.PostAsJsonAsync("/oauth/register", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var uris = doc.RootElement.GetProperty("redirect_uris").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        uris.Should().BeEquivalentTo(new[] { "http://localhost:51234/callback" },
            "the evil URL must not be echoed back");
    }

    [Theory]
    [InlineData("client_credentials")]
    [InlineData("password")]
    [InlineData("urn:ietf:params:oauth:grant-type:device_code")]
    public async Task Token_RejectsUnsupportedGrantType(string grantType)
    {
        // The proxy injects a confidential client_secret on the way upstream. If it forwarded
        // an arbitrary grant (e.g. client_credentials), a caller could obtain a token for our
        // audience with no user sign-in. The guard must reject before any upstream call.
        using var client = _factory.CreateClient();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = "test-client-id"
        });
        var response = await client.PostAsync("/oauth/token", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("unsupported_grant_type");
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
                    // Bypass JWT validation — proxy endpoints don't require auth and we don't
                    // want to stand up a real OIDC provider for the test. NoAuth still permits
                    // the proxy when SharedClientId is set.
                    ["OAuth:NoAuth"] = "true",
                    ["OAuth:Authority"] = "https://example.auth0.com/",
                    ["OAuth:Audience"] = "https://vitally.example.com",
                    ["OAuth:SharedClientId"] = "test-client-id",
                    ["OAuth:AllowedClientRedirectUris:0"] = "https://claude.ai/api/mcp/auth_callback",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                });
            });
            return base.CreateHost(builder);
        }
    }
}
