using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

/// <summary>
/// The proxy with <c>OAuth:UpstreamResourceScope</c> set — the Entra posture (#105 part B / #108).
/// The sibling proxy classes cover the Auth0 posture, where <c>resource</c> is validated and then
/// relayed; here it is validated and then <b>terminated</b>, with the configured scope carrying the
/// same meaning upstream.
/// </summary>
/// <remarks>
/// A separate fixture rather than extra cases on the existing ones, because the switch is a
/// composition-time option and the two postures must both stay pinned: the Auth0 relay is still live
/// in production until the cutover deploys, and it is what a rollback returns to.
/// </remarks>
public class OAuthProxyResourceTerminationTests : IClassFixture<OAuthProxyResourceTerminationTests.Factory>
{
    /// <summary>The App ID URI + exposed scope, in the shape Entra requires on a custom API.</summary>
    private const string ApiScope = "https://vitally.example.com/mcp.access";

    private readonly Factory _factory;

    public OAuthProxyResourceTerminationTests(Factory factory) => _factory = factory;

    private static HttpClient NoRedirect(Factory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Theory]
    [InlineData("https://vitally.example.com")]
    [InlineData("https://vitally.example.com/")]
    public async Task Authorize_DropsAMatchingResourceInsteadOfForwardingIt(string resource)
    {
        // The failure this prevents is AADSTS9010010, and it is not slash-specific: Entra's v2
        // authorize endpoint refuses any `resource` that does not match the requested scopes, and
        // against the live tenant the slashed form, the exact App ID URI and an unregistered value
        // all returned 400 alike. Both spellings are exercised here because
        // IsResourceIndicatorAllowed accepts both — so both reach this point, and the parameter has
        // to disappear whichever one arrives.
        using var client = NoRedirect(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=drop-resource"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&resource=" + Uri.EscapeDataString(resource));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query).Should().NotContainKey("resource");
    }

    [Fact]
    public async Task Authorize_DropsAResourceWhateverItsCasing()
    {
        // IQueryCollection lookups are case-insensitive so an oddly-cased key is *validated*, but
        // enumeration yields keys as parsed — an exact-match skip would forward `RESOURCE` intact
        // and undo the whole change for that request.
        using var client = NoRedirect(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=drop-resource-cased"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&RESOURCE=" + Uri.EscapeDataString("https://vitally.example.com/"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        QueryHelpers.ParseQuery(response.Headers.Location!.Query).Keys
            .Should().NotContain(k => string.Equals(k, "resource", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authorize_StillRejectsAResourceWeDoNotPublish()
    {
        // Terminating the parameter must not quietly become ignoring it. The check is about which
        // audience a caller may ask to be bound to, so it survives the change unaltered.
        using var client = NoRedirect(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=still-validated"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&resource=" + Uri.EscapeDataString("https://evil.example.com/"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_target");
    }

    [Fact]
    public async Task Authorize_AppendsTheApiScopeToWhatTheClientAskedFor()
    {
        using var client = NoRedirect(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=scope-appended"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&scope=" + Uri.EscapeDataString("openid profile offline_access"));

        var scope = QueryHelpers.ParseQuery(response.Headers.Location!.Query)["scope"];
        scope.Count.Should().Be(1, "two scope parameters is a malformed request upstream");
        scope[0]!.Split(' ').Should().BeEquivalentTo(
            new[] { "openid", "profile", "offline_access", ApiScope },
            "the client's own scopes must survive — dropping offline_access would cost the refresh token");
    }

    [Fact]
    public async Task Authorize_SendsTheApiScopeEvenWhenTheClientAsksForNone()
    {
        // Without this the access token names no resource at all: the proxy sends no `audience`
        // parameter anywhere, and `resource` is no longer forwarded either.
        using var client = NoRedirect(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=scope-invented"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback"));

        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["scope"].ToString().Should().Be(ApiScope);
    }

    [Fact]
    public async Task Authorize_DoesNotRepeatTheApiScopeAClientAlreadyRequested()
    {
        // Clients build their request from `scopes_supported`, which now advertises this exact
        // value — so the common case is that it is already there.
        using var client = NoRedirect(_factory);

        var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=test&state=scope-deduped"
            + "&redirect_uri=" + Uri.EscapeDataString("http://localhost:54321/callback")
            + "&scope=" + Uri.EscapeDataString("openid " + ApiScope));

        QueryHelpers.ParseQuery(response.Headers.Location!.Query)["scope"].ToString()
            .Should().Be("openid " + ApiScope);
    }

    [Fact]
    public async Task Token_DropsResourceAndSendsTheApiScopeInstead()
    {
        using var client = _factory.CreateClient();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = "test-code",
            ["redirect_uri"] = "http://localhost:54321/callback",
            ["resource"] = "https://vitally.example.com/"
        });
        var response = await client.PostAsync("/oauth/token", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = _factory.Upstream.RequestBodies.Last();
        body.Should().NotContain("resource=");
        body.Should().Contain("scope=" + Uri.EscapeDataString(ApiScope));
    }

    [Fact]
    public async Task Token_NamesTheApiOnARefreshExchangeTheClientScopedToOidcOnly()
    {
        // The load-bearing case for merging on the token endpoint as well. Entra issues the new
        // access token for whatever resource `scope` names, so a refresh carrying only the OIDC
        // scopes would silently hand back a token for the wrong audience — which fails at /mcp,
        // hours after the sign-in that looked fine.
        using var client = _factory.CreateClient();

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "test-refresh",
            ["scope"] = "openid profile offline_access"
        });
        var response = await client.PostAsync("/oauth/token", form);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = _factory.Upstream.RequestBodies.Last();
        var scope = QueryHelpers.ParseQuery("?" + body)["scope"];
        scope.Count.Should().Be(1);
        scope[0]!.Split(' ').Should().Contain(ApiScope);
    }

    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task Metadata_AdvertisesTheApiScopeInTheFormTheProviderAccepts(string path)
    {
        // Both documents, because clients read `scopes_supported` from whichever they reached and
        // build their `scope` request from it. Advertising a bare `mcp.access` while forwarding to
        // Entra breaks the flow before sign-in: a scope on a custom API must carry the App ID URI.
        using var client = _factory.CreateClient();

        using var doc = JsonDocument.Parse(await client.GetStringAsync(path));

        var scopes = doc.RootElement.GetProperty("scopes_supported")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        scopes.Should().Contain(ApiScope);
        scopes.Should().NotContain("mcp.access", "the bare name is the Auth0 spelling and Entra rejects it");
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
                    // Audience without the slash and Resource with it, as they diverge under Entra:
                    // Audience follows the App ID URI (Entra refuses to register a trailing slash),
                    // Resource stays the form clients normalise to. IsResourceIndicatorAllowed
                    // tolerating exactly one slash is what lets both spellings name one resource.
                    ["OAuth:Audience"] = "https://vitally.example.com",
                    ["OAuth:Resource"] = "https://vitally.example.com/",
                    ["OAuth:UpstreamResourceScope"] = ApiScope,
                    ["OAuth:SharedClientId"] = "test-client-id",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.UseStubDiscovery();
                services.AddHttpClient(Options.DefaultName)
                    .ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
            return base.CreateHost(builder);
        }
    }
}
