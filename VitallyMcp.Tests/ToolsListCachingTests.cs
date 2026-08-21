using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// Asserts the serialised wire form of the tools/list cache hints added in the 2026-07-28 spec.
/// The property names are deliberately asserted as raw JSON (`ttlMs`, `cacheScope`) rather than
/// via the SDK's CLR properties, so a future SDK rename is caught here rather than by clients.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ToolsListCachingTests : IClassFixture<ToolsListCachingTests.Factory>
{
    private readonly Factory _factory;

    public ToolsListCachingTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_AdvertisesTtlAndPrivateCacheScope()
    {
        using var client = _factory.CreateClient();

        var body = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8,
            "application/json");
        body.Headers.Remove("Content-Type");
        body.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = body };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = ExtractJson(text);

        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("result");

        result.TryGetProperty("ttlMs", out var ttl).Should().BeTrue("the 2026-07-28 cache hint must be present");
        ttl.GetInt64().Should().Be(300_000, "the default TTL is 5 minutes expressed in milliseconds");

        result.TryGetProperty("cacheScope", out var scope).Should().BeTrue();
        scope.GetString().Should().Be("private",
            "the list is per-caller once authorization filtering is enabled, so it must not be cached publicly");
    }

    /// <summary>Streamable HTTP may frame the response as SSE; pull the JSON payload out either way.</summary>
    private static string ExtractJson(string raw)
    {
        if (!raw.Contains("data:", StringComparison.Ordinal))
        {
            return raw;
        }

        var line = raw.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal));
        return line["data:".Length..].Trim();
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Program.cs reads these at composition time, before WebApplicationFactory can inject
            // configuration, so environment variables are the only reliable override. Same
            // constraint documented in ReadOnlyToolsListTests. Every variable this fixture depends
            // on is set explicitly — they are process-wide, so a value left behind by a sibling
            // class must be overwritten rather than inherited.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "true");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            return base.CreateHost(builder);
        }
    }
}
