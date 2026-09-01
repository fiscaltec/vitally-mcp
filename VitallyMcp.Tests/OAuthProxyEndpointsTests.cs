using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        // Accepted requests are 302-redirected to the upstream authorization endpoint — the one the
        // provider publishes in its discovery document, not one assembled from OAuth:Authority.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.ToString().Should().StartWith(StubOidcDiscovery.AuthorizationEndpoint + "?");
    }

    [Fact]
    public async Task Authorize_SendsTheUserToTheDiscoveredAuthorizationEndpoint()
    {
        // The stub's authorization_endpoint sits on a different host and path from OAuth:Authority,
        // so this fails if anyone reinstates the `{authority}/authorize` concatenation.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=discovery&redirect_uri="
            + Uri.EscapeDataString("http://localhost:54321/callback"));

        var location = response.Headers.Location!.ToString();
        location.Should().StartWith(StubOidcDiscovery.AuthorizationEndpoint + "?");
        location.Should().NotContain("example.auth0.com");
        // The proxy's own callback still has to survive the rewrite, or the code never comes back.
        location.Should().Contain(Uri.EscapeDataString("http://localhost/oauth/callback"));
    }

    [Fact]
    public async Task AuthorizationServerMetadata_PublishesTheDiscoveredJwksAndUserInfoEndpoints()
    {
        // These two are the reason this is a correctness fix and not just tidying: they are handed
        // to every MCP client as fact. `userinfo_endpoint` is on a host that no choice of
        // OAuth:Authority could ever produce.
        using var client = _factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        doc.RootElement.GetProperty("jwks_uri").GetString().Should().Be(StubOidcDiscovery.JwksUri);
        doc.RootElement.GetProperty("userinfo_endpoint").GetString().Should().Be(StubOidcDiscovery.UserInfoEndpoint);
    }

    [Fact]
    public async Task AuthorizationServerMetadata_StillNamesOurOwnOriginForTheEndpointsWeFront()
    {
        // Guards the discovery change against over-reach. authorize, token and register are the
        // façade's own — pointing any of them upstream would break DCR and the `iss` contract.
        using var client = _factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        doc.RootElement.GetProperty("authorization_endpoint").GetString().Should().Be("http://localhost/oauth/authorize");
        doc.RootElement.GetProperty("token_endpoint").GetString().Should().Be("http://localhost/oauth/token");
        doc.RootElement.GetProperty("registration_endpoint").GetString().Should().Be("http://localhost/oauth/register");
    }

    [Theory]
    [InlineData("https://vitally.example.com")]
    [InlineData("https://vitally.example.com/")]
    public async Task Authorize_ForwardsAMatchingResourceUnchanged(string resource)
    {
        // `resource` still goes upstream: while Auth0 is the authority, the tenant's Resource
        // Parameter Compatibility Profile consumes it locally and it is the only thing binding
        // the issued token's audience (Program.cs sends no `audience` parameter anywhere).
        // Terminating it here is #108's job, once Authority points at Entra.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=res-ok"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&resource=" + Uri.EscapeDataString(resource));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var forwarded = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        forwarded["resource"].ToString().Should().Be(resource,
            "the value must reach the upstream provider exactly as the client sent it");
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("https://vitally.example.com.evil.com/")]
    [InlineData("https://vitally.example.com/other")]
    [InlineData("")]
    public async Task Authorize_RejectsAResourceWeDoNotPublish(string resource)
    {
        // RFC 8707 `resource` is an audience-binding control, and it arrived unvalidated: the
        // proxy forwarded the client's query string verbatim. Accepting a value we never
        // published in our RFC 9728 document would let a client obtain a token bound to an
        // audience this server does not vouch for.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=res-bad"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&resource=" + Uri.EscapeDataString(resource));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_target");
    }

    [Fact]
    public async Task Authorize_WithoutAResourceIsUnaffected()
    {
        // RFC 8707 is optional for clients. Validation must not turn an absent parameter into
        // a failure, and must not invent one on the way upstream.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=res-absent"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query)
            .Should().NotContainKey("resource");
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

    [Fact]
    public async Task AuthorizationServerMetadata_DeclaresTheServingOriginAsIssuer()
    {
        // RFC 8414 §3.3: the `issuer` in the document must be identical to the issuer identifier
        // used to build the well-known URL. It is an anti-mix-up control — without it, anyone able
        // to serve you a metadata document could point you at another issuer's endpoints while you
        // believe you are talking to the issuer you trust. We serve this document from our own
        // origin and front /oauth/authorize, /oauth/token and /oauth/register ourselves, so our own
        // origin is the honest answer; declaring Auth0's made strict clients (the TypeScript MCP
        // SDK, hence MCP Inspector) abort before ever reaching DCR.
        using var client = _factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        doc.RootElement.GetProperty("issuer").GetString().Should().Be("http://localhost");
        doc.RootElement.GetProperty("issuer").GetString().Should().NotContain("auth0.com",
            "the upstream authority still issues the tokens, but it is not what this document speaks for");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task AuthorizationServerMetadata_IssuerMatchesTheAuthorizationServerAdvertisedToClients(string resourceMetadataPath)
    {
        // The pairing a strict client actually checks: it reads `authorization_servers` out of the
        // protected-resource document, uses that string verbatim to build the well-known URL, and
        // requires the returned `issuer` to equal it. Asserting each document in isolation would
        // let the two drift apart while both still looked correct, so pin them together.
        using var client = _factory.CreateClient();

        using var resourceDoc = JsonDocument.Parse(await client.GetStringAsync(resourceMetadataPath));
        var advertisedAuthorizationServer = resourceDoc.RootElement
            .GetProperty("authorization_servers").EnumerateArray().Single().GetString();

        using var asDoc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        asDoc.RootElement.GetProperty("issuer").GetString().Should().Be(advertisedAuthorizationServer);
    }

    [Fact]
    public async Task AuthorizationServerMetadata_AdvertisesIssParameterSupport()
    {
        // Honest only because /oauth/callback injects `iss` unconditionally. Advertising support
        // while omitting the parameter is itself an error a strict client reports, so this
        // assertion and Callback_AddsIssMatchingTheMetadataIssuer are two halves of one contract.
        using var client = _factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));

        doc.RootElement.GetProperty("authorization_response_iss_parameter_supported").GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public async Task Callback_AddsIssMatchingTheMetadataIssuer()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var metadata = JsonDocument.Parse(await client.GetStringAsync("/.well-known/oauth-authorization-server"));
        var expectedIssuer = metadata.RootElement.GetProperty("issuer").GetString();

        var location = await AuthorizeThenCallbackAsync(client, state: "iss-added");

        var iss = QueryHelpers.ParseQuery(location.Query)["iss"];
        iss.Count.Should().Be(1);
        iss[0].Should().Be(expectedIssuer,
            "clients compare the two with simple string equality — no trailing-slash or percent-encoding normalisation");
    }

    [Fact]
    public async Task Callback_ReplacesAnUpstreamIssWithOurOwn()
    {
        // Auth0 sends `iss` naming itself when the tenant is configured for RFC 9207, and whether
        // it does is tenant configuration we do not control. Forwarding that value — or appending
        // ours alongside it — breaks strict clients: the SDK compares a *present* `iss` against the
        // metadata issuer even when support is not advertised. So the upstream value must be
        // dropped rather than kept, and there must be exactly one `iss` on the way out.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var location = await AuthorizeThenCallbackAsync(client, state: "iss-replaced",
            upstreamExtras: "&iss=" + Uri.EscapeDataString("https://example.auth0.com/"));

        var iss = QueryHelpers.ParseQuery(location.Query)["iss"];
        iss.Count.Should().Be(1, "a duplicated parameter lets a client read whichever one it happens to pick first");
        iss[0].Should().Be("http://localhost");
        location.Query.Should().NotContain("auth0.com");
    }

    [Fact]
    public async Task Callback_ReplacesAnUpstreamIssRegardlessOfItsCasing()
    {
        // IQueryCollection lookups are case-insensitive but enumeration preserves the casing as
        // parsed, so an exact-match skip would forward a differently-cased `ISS` alongside ours.
        // A conformant client would never read it — OAuth parameter names are case-sensitive, so
        // only the lowercase `iss` is ever compared — but leaving it there means the next reader
        // has to derive that for themselves before concluding the redirect is safe.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var location = await AuthorizeThenCallbackAsync(client, state: "iss-mixed-case",
            upstreamExtras: "&ISS=" + Uri.EscapeDataString("https://example.auth0.com/"));

        var issLike = QueryHelpers.ParseQuery(location.Query)
            .Where(kv => string.Equals(kv.Key, "iss", StringComparison.OrdinalIgnoreCase))
            .SelectMany(kv => kv.Value.ToArray())
            .ToArray();

        issLike.Should().BeEquivalentTo(new[] { "http://localhost" });
    }

    [Fact]
    public async Task Callback_PreservesTheAuthorizationCodeAndState()
    {
        // Guards the iss handling against over-reach: rewriting the query string must not disturb
        // the parameters the client actually needs to complete the code exchange.
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var location = await AuthorizeThenCallbackAsync(client, state: "iss-passthrough");

        var query = QueryHelpers.ParseQuery(location.Query);
        query["code"].ToString().Should().Be("test-code");
        query["state"].ToString().Should().Be("iss-passthrough");
    }

    /// <summary>
    /// Drives the real two-hop proxy flow: /oauth/authorize stores the state→redirect_uri mapping
    /// that /oauth/callback reverses, so the callback cannot be exercised in isolation. Returns the
    /// absolute URI the callback redirects the client to.
    /// </summary>
    private static async Task<Uri> AuthorizeThenCallbackAsync(HttpClient client, string state, string upstreamExtras = "")
    {
        const string clientRedirectUri = "http://localhost:54321/callback";

        var authorize = await client.GetAsync(
            $"/oauth/authorize?response_type=code&client_id=test&state={state}&redirect_uri={Uri.EscapeDataString(clientRedirectUri)}");
        authorize.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var callback = await client.GetAsync($"/oauth/callback?code=test-code&state={state}{upstreamExtras}");
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var location = callback.Headers.Location;
        location.Should().NotBeNull();
        location!.ToString().Should().StartWith(clientRedirectUri);
        return location;
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
                    // Must equal StubOidcDiscovery.Issuer — the resolver refuses a document that
                    // speaks for a different issuer (OIDC Discovery §4.3).
                    ["OAuth:Authority"] = StubOidcDiscovery.Issuer,
                    ["OAuth:Audience"] = "https://vitally.example.com",
                    ["OAuth:SharedClientId"] = "test-client-id",
                    ["OAuth:AllowedClientRedirectUris:0"] = "https://claude.ai/api/mcp/auth_callback",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                });
            });
            // The proxy resolves the upstream endpoints from the provider's discovery document at
            // startup and refuses to boot without it, so a stub is mandatory here rather than a
            // convenience — without one the host would try to reach example.auth0.com.
            builder.ConfigureServices(services => services.UseStubDiscovery());
            return base.CreateHost(builder);
        }
    }
}
