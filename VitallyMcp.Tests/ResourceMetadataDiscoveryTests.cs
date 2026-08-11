using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// MCP requires an unauthenticated request to a protected endpoint to answer 401 with a
/// WWW-Authenticate header pointing at the protected-resource metadata document. Without that
/// pointer, clients can only guess the well-known location — which is how discovery silently
/// depends on a fallback today.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ResourceMetadataDiscoveryTests : IClassFixture<ResourceMetadataDiscoveryTests.Factory>
{
    private readonly Factory _factory;

    public ResourceMetadataDiscoveryTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task UnauthenticatedMcpCall_Returns401WithResourceMetadataPointer()
    {
        using var client = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the deploy.yml smoke check asserts 401 — this must not change");

        // Exactly one challenge value — not Contains. A second, bare "Bearer" challenge
        // appended alongside ours is HTTP-legal but lets a client that reads only one of the
        // two values (first or last) see no resource_metadata pointer at all, which is the
        // discovery failure this task exists to close. Contains alone let that regression
        // through unnoticed; asserting the count is the point.
        response.Headers.WwwAuthenticate.Should().HaveCount(1);

        var challenge = response.Headers.WwwAuthenticate.Single().ToString();
        challenge.Should().Contain("resource_metadata",
            "MCP clients use this parameter to locate the protected-resource metadata document");
        challenge.Should().Contain("/.well-known/oauth-protected-resource");
    }

    [Fact]
    public async Task InvalidBearerToken_Returns401WithBothResourceMetadataAndInvalidTokenError()
    {
        using var client = _factory.CreateClient();

        // Syntactically valid JWT shape (header.payload.signature, all base64url) but an
        // unverifiable signature — enough to reach JwtBearerHandler's failed-validation path
        // without needing a real Auth0 tenant.
        const string junkJwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0In0.aW52YWxpZC1zaWduYXR1cmU";

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", junkJwt);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().HaveCount(1);

        var challenge = response.Headers.WwwAuthenticate.Single().ToString();
        challenge.Should().Contain("resource_metadata",
            "the pointer must still be present even when the failure is an invalid token, not just a missing one");
        challenge.Should().Contain("error=\"invalid_token\"");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task BothMetadataPaths_ReturnTheSameDocument(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} must be served");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("resource").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("authorization_servers").EnumerateArray().Should().NotBeEmpty();
        root.GetProperty("bearer_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("header");
        root.TryGetProperty("scopes_supported", out var scopes).Should().BeTrue(
            "clients use this to request the right scopes up front");
        scopes.EnumerateArray().Should().NotBeEmpty();
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Auth ON — this fixture is specifically about the 401 challenge. Every variable set
            // explicitly because they are process-wide (see IntegrationTestCollection).
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "false");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("OAuth__Authority", "https://example.auth0.com/");
            Environment.SetEnvironmentVariable("OAuth__Audience", "https://example.test/");
            Environment.SetEnvironmentVariable("OAuth__Resource", "https://example.test/");
            Environment.SetEnvironmentVariable("OAuth__PublicBaseUrl", "https://example.test");
            return base.CreateHost(builder);
        }
    }
}
