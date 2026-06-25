using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// Integration guard: the MCP <c>initialize</c> response carries the server-level
/// <c>instructions</c> guidance (<see cref="VitallyServerInstructions"/>), proving it is wired into
/// the host end-to-end via <c>McpServerOptions.ServerInstructions</c>.
/// </summary>
public class ServerInstructionsInitializeTests : IClassFixture<ServerInstructionsInitializeTests.Factory>
{
    private readonly Factory _factory;

    public ServerInstructionsInitializeTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task Initialize_ReturnsServerInstructions()
    {
        using var client = _factory.CreateClient();

        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "test", version = "0.0.1" }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // Body may be SSE-framed (data: {…}); the JSON-RPC result carries result.instructions.
        var json = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimStart())
            .Where(l => l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["data:".Length..].Trim())
            .FirstOrDefault(c => c.StartsWith('{')) ?? text.Trim();

        using var doc = JsonDocument.Parse(json);
        var instructions = doc.RootElement.GetProperty("result").GetProperty("instructions").GetString();

        instructions.Should().NotBeNullOrWhiteSpace();
        instructions.Should().Contain("organisation");
        instructions.Should().Contain("custom object");
    }

    public class Factory : WebApplicationFactory<Program>
    {
        public Factory()
        {
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "true");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            Environment.SetEnvironmentVariable("OAuth__NoAuth", null);
            Environment.SetEnvironmentVariable("Vitally__Region", null);
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", null);
            base.Dispose(disposing);
        }
    }
}
