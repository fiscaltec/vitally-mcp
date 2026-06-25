using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// Integration guard: proves that when <c>Authorization:ReadOnly=true</c> is set, the MCP
/// <c>tools/list</c> response hides destructive tools (<c>Create_*</c>, <c>Update_*</c>,
/// <c>Delete_*</c>) while still advertising read tools (<c>List_*</c>, <c>Get_*</c>).
///
/// This guards the <see cref="Program"/> <c>AddListToolsFilter</c> wiring in
/// <c>Program.cs</c> against a future refactor silently dropping it. The filter logic itself
/// is unit-tested in <see cref="ReadOnlyToolFilterTests"/>; this test covers the end-to-end
/// wiring through <see cref="WebApplicationFactory{TEntryPoint}"/>.
///
/// <para>
/// Implementation note: <c>Program.cs</c> reads <c>OAuth:NoAuth</c> and
/// <c>Authorization:ReadOnly</c> as local variables at app-composition time. Because
/// <c>WebApplicationFactory</c> intercepts the <c>IHostBuilder</c> only after the application's
/// top-level code has already run once, <c>ConfigureAppConfiguration</c> overrides are too late
/// to affect those variables. Environment variables are the reliable alternative — they are
/// consumed by <c>WebApplication.CreateBuilder</c> as part of its default provider chain before
/// any code in <c>Program.cs</c> runs, so the read-only and no-auth flags are set correctly for
/// the test process.
/// </para>
/// </summary>
public class ReadOnlyToolsListTests : IClassFixture<ReadOnlyToolsListTests.Factory>
{
    private readonly Factory _factory;

    public ReadOnlyToolsListTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ToolsList_InReadOnlyMode_HidesDestructiveToolsAndKeepsReadTools()
    {
        using var client = _factory.CreateClient();

        // The MCP streamable-HTTP transport is configured as stateless, so tools/list can be
        // sent as a standalone request without a prior initialise handshake.
        var toolNames = await GetToolNamesAsync(client);

        // --- positive assertion: read tools must be present ---
        toolNames.Should().Contain(n => n.StartsWith("List_") || n.StartsWith("Get_"),
            "read-only mode must still expose List_* and Get_* tools");

        // --- negative assertion: destructive tools must be absent ---
        toolNames.Should().NotContain(n => n.StartsWith("Create_"),
            "Create_* tools must be hidden in read-only mode");
        toolNames.Should().NotContain(n => n.StartsWith("Update_"),
            "Update_* tools must be hidden in read-only mode");
        toolNames.Should().NotContain(n => n.StartsWith("Delete_"),
            "Delete_* tools must be hidden in read-only mode");
    }

    /// <summary>
    /// Sends a <c>tools/list</c> JSON-RPC request and returns the list of tool names.
    /// Handles the SSE framing that the MCP streamable-HTTP transport uses.
    /// If the server returns a JSON-RPC error, an <c>initialize</c> handshake is attempted
    /// first, then <c>tools/list</c> is retried.
    /// </summary>
    private static async Task<IReadOnlyList<string>> GetToolNamesAsync(HttpClient client)
    {
        var toolsListBody = BuildJsonRpc("tools/list", id: 1);

        var responseText = await PostMcpAsync(client, toolsListBody);
        var json = ExtractJsonFromSseOrRaw(responseText);

        // If the server returned a JSON-RPC error, attempt an initialize handshake first.
        using var probe = JsonDocument.Parse(json);
        if (probe.RootElement.TryGetProperty("error", out _))
        {
            var initBody = BuildJsonRpc("initialize", id: 0, new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "test", version = "0.0.1" }
            });
            _ = await PostMcpAsync(client, initBody);

            responseText = await PostMcpAsync(client, toolsListBody);
            json = ExtractJsonFromSseOrRaw(responseText);
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? string.Empty)
            .ToList();
    }

    private static async Task<string> PostMcpAsync(HttpClient client, string jsonBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    private static string BuildJsonRpc(string method, int id, object? @params = null)
    {
        var obj = @params is null
            ? new { jsonrpc = "2.0", id, method }
            : (object)new { jsonrpc = "2.0", id, method, @params };
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>
    /// Extracts the first JSON object from a response body that may be plain JSON or an
    /// SSE stream. SSE lines look like <c>data: {…json…}</c>; everything else is returned
    /// as-is after trimming.
    /// </summary>
    private static string ExtractJsonFromSseOrRaw(string body)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = trimmed["data:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.StartsWith('{'))
                    return candidate;
            }
        }

        return body.Trim();
    }

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> configured for read-only / no-auth mode.
    /// <para>
    /// Environment variables are set in the constructor (before <c>CreateHost</c> is invoked
    /// by <c>WebApplicationFactory</c>) so that <c>Program.cs</c>'s top-level variable reads
    /// (e.g. <c>noAuth</c>, <c>readOnlyMode</c>) pick up the correct values. They are cleaned
    /// up in <see cref="Dispose"/>. The corresponding <c>AddInMemoryCollection</c> override
    /// in <c>CreateHost</c> is belt-and-braces for configuration bindings resolved later via
    /// <c>IOptions</c>.
    /// </para>
    /// </summary>
    public class Factory : WebApplicationFactory<Program>
    {
        public Factory()
        {
            // Set env vars before the base class creates the test server so Program.cs
            // reads the correct values at app-composition time.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "true");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "true");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // Belt-and-braces: also override via in-memory config for IOptions bindings.
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OAuth:NoAuth"] = "true",
                    ["Authorization:ReadOnly"] = "true",
                    ["Vitally:Region"] = "EU",
                    ["Vitally:DevelopmentApiKey"] = "sk_test_dummy"
                });
            });
            return base.CreateHost(builder);
        }

        protected override void Dispose(bool disposing)
        {
            // Clean up so env vars don't leak into other test classes.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", null);
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", null);
            Environment.SetEnvironmentVariable("Vitally__Region", null);
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", null);
            base.Dispose(disposing);
        }
    }
}
