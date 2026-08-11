using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VitallyMcp.Tests;

/// <summary>
/// Proves per-caller tools/list filtering: a reader must not be shown mutating tools, an editor
/// must see create/update but not delete, and an admin must see everything.
///
/// This is the load-bearing test for the AddAuthorizationFilters() adoption. Staging only confirms
/// it against real Entra groups; correctness is established here.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthorizationFilterToolsListTests
{
    private static Task<IReadOnlyList<string>> ToolNamesForAsync(params string[] permissions)
        => ToolNamesAsync(noAuth: false, permissions);

    private static async Task<IReadOnlyList<string>> ToolNamesAsync(bool noAuth, string[] permissions)
    {
        var json = await PostMcpAsync(noAuth, permissions, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeFalse(
            $"tools/list must succeed for a caller holding [{string.Join(", ", permissions)}], but the server returned {error}");

        return doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    /// <summary>
    /// Composes a host at the given auth posture, posts one JSON-RPC message to <c>/mcp</c> and
    /// returns the JSON payload (unwrapping SSE framing if the transport used it).
    /// </summary>
    /// <param name="auditSink">
    /// When supplied, is attached to the host's logging so the caller can assert on what
    /// <see cref="AuditLogger"/> recorded during the request.
    /// </param>
    private static async Task<string> PostMcpAsync(
        bool noAuth,
        string[] permissions,
        string jsonRpcBody,
        CapturingLoggerProvider? auditSink = null)
    {
        // All process-wide, so set every one explicitly. Authorization__ReadOnly MUST be false here:
        // if a sibling class leaves it true, ReadOnlyToolFilter strips every destructive tool and the
        // editor/admin assertions below fail for the wrong reason.
        Environment.SetEnvironmentVariable("OAuth__NoAuth", noAuth ? "true" : "false");
        Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
        Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
        Environment.SetEnvironmentVariable("Vitally__Region", "EU");
        Environment.SetEnvironmentVariable("OAuth__Authority", noAuth ? null : "https://example.auth0.com/");
        Environment.SetEnvironmentVariable("OAuth__Audience", noAuth ? null : "https://example.test/");

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(b =>
                {
                    if (auditSink is not null)
                    {
                        b.ConfigureLogging(logging => logging.AddProvider(auditSink));
                    }

                    b.ConfigureServices(services =>
                    {
                        if (noAuth)
                        {
                            // Dev mode registers no authentication scheme at all; leave it that way
                            // so the test exercises the real local-development composition.
                            return;
                        }

                        // Replace the JwtBearer default with a scheme that authenticates as our
                        // synthetic principal, so real policy evaluation runs against known permissions.
                        services.AddAuthentication(TestAuthHandler.SchemeName)
                            .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                                TestAuthHandler.SchemeName, o => o.Permissions = permissions);

                        services.Configure<AuthorizationOptions>(o =>
                            o.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                                .RequireAuthenticatedUser().Build());
                    });
                });

            using var client = factory.CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(jsonRpcBody, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var response = await client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            return raw.Contains("data:", StringComparison.Ordinal)
                ? raw.Split('\n').First(l => l.TrimStart().StartsWith("data:", StringComparison.Ordinal)).Trim()["data:".Length..].Trim()
                : raw;
        }
        finally
        {
            // Don't leak the auth-on configuration into the sibling classes in this collection —
            // they all compose hosts expecting NoAuth=true.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", null);
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", null);
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", null);
            Environment.SetEnvironmentVariable("Vitally__Region", null);
            Environment.SetEnvironmentVariable("OAuth__Authority", null);
            Environment.SetEnvironmentVariable("OAuth__Audience", null);
        }
    }

    [Fact]
    public async Task Reader_SeesOnlyReadTools()
    {
        var names = await ToolNamesForAsync("vitally:read");

        names.Should().Contain(n => n.StartsWith("List_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Create_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Update_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Delete_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editor_SeesWriteToolsButNotDeletes()
    {
        var names = await ToolNamesForAsync("vitally:read", "vitally:write");

        names.Should().Contain(n => n.StartsWith("Create_", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("Update_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Delete_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_SeesEveryTool()
    {
        var names = await ToolNamesForAsync("vitally:read", "vitally:write", "vitally:delete");

        names.Should().Contain(n => n.StartsWith("Delete_", StringComparison.Ordinal));
        names.Should().HaveCountGreaterThan(90);
    }

    /// <summary>
    /// The tiers are cumulative and exactly partition the 93 tools: read-only 56, plus writes 25,
    /// plus deletes 12. Asserting the counts (not just presence/absence of prefixes) is what catches
    /// an off-tier tool — e.g. <c>Add_meeting_participant</c>, which is neither <c>Create_*</c> nor
    /// <c>Update_*</c> yet issues a POST, so it must appear for the editor and not for the reader.
    /// </summary>
    [Fact]
    public async Task TierCounts_PartitionEveryTool()
    {
        var reader = await ToolNamesForAsync("vitally:read");
        var editor = await ToolNamesForAsync("vitally:read", "vitally:write");
        var admin = await ToolNamesForAsync("vitally:read", "vitally:write", "vitally:delete");

        reader.Should().HaveCount(56);
        editor.Should().HaveCount(81);
        admin.Should().HaveCount(93);

        reader.Should().BeSubsetOf(editor);
        editor.Should().BeSubsetOf(admin);

        editor.Should().Contain("Add_meeting_participant");
        reader.Should().NotContain("Add_meeting_participant");
        admin.Should().Contain("Remove_meeting_participant");
        editor.Should().NotContain("Remove_meeting_participant");
    }

    /// <summary>
    /// A caller who authenticates but holds no <c>vitally:*</c> permission at all must be shown
    /// nothing. This is the other side of the risk: a filter that fails open would advertise all 93.
    /// </summary>
    [Fact]
    public async Task CallerWithNoPermissions_SeesNoTools()
    {
        var names = await ToolNamesForAsync();

        names.Should().BeEmpty();
    }

    /// <summary>
    /// Local development (<c>OAuth:NoAuth=true</c>) must still see the complete tool list. Two
    /// distinct ways to break this, both proven to occur:
    /// <list type="bullet">
    /// <item>Skipping <c>AddAuthorizationFilters()</c> under NoAuth: the SDK fails closed on tools
    /// carrying authorisation metadata, so <c>tools/list</c> returns a JSON-RPC error rather than an
    /// empty list.</item>
    /// <item>Registering the filters but not bypassing the policy: every policy fails against the
    /// anonymous dev principal and the list comes back empty.</item>
    /// </list>
    /// Asserting the full count catches both, where a prefix-presence assertion catches neither.
    /// </summary>
    [Fact]
    public async Task NoAuthDevMode_SeesEveryToolUnfiltered()
    {
        var names = await ToolNamesAsync(noAuth: true, []);

        names.Should().HaveCount(93);
    }

    /// <summary>
    /// Hiding a tool from <c>tools/list</c> must not be the only thing stopping a reader from calling
    /// it. A client that already cached the full list, or one that simply guesses the name, still
    /// reaches <c>tools/call</c> — so this asserts the call is refused rather than executed. Enforcement
    /// remains layered: the SDK's call-tool checkpoint here, and <c>VitallyService.SendAsync</c>
    /// behind it (covered by <see cref="VitallyServiceAuthorizationTests"/>).
    /// </summary>
    [Fact]
    public async Task Reader_CallingAWriteTool_IsDenied()
    {
        var json = await PostMcpAsync(noAuth: false, ["vitally:read"],
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Create_organization","arguments":{"jsonBody":"{}"}}}""");

        // The SDK's call-tool checkpoint refuses before the handler runs, so this is a JSON-RPC
        // error rather than a tool result. A successful call would have attempted the upstream
        // Vitally POST against the dummy key.
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("error", out var error).Should().BeTrue(
            "a reader must not be able to invoke a write tool even by naming it directly");
        error.GetProperty("message").GetString().Should().Contain("Access forbidden");
        json.Should().NotContain("\"result\"");
    }

    /// <summary>
    /// A refused out-of-tier call must leave exactly one audit record. This is not incidental: the
    /// SDK's checkpoint rejects the call before <c>VitallyService.SendAsync</c> runs, so
    /// <c>AuditLogger.LogDenied</c> — whose only call site is inside <c>SendAsync</c> — never fires
    /// for a tier mismatch. <c>LogToolCallDenied</c> exists to close that gap, and "exactly one"
    /// is what proves the two hooks do not both fire.
    /// </summary>
    [Fact]
    public async Task DeniedOutOfTierCall_ProducesExactlyOneAuditDenyRecord()
    {
        var auditSink = new CapturingLoggerProvider(typeof(AuditLogger).FullName!);

        await PostMcpAsync(noAuth: false, ["vitally:read"],
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Delete_account","arguments":{"id":"acc-1"}}}""",
            auditSink);

        var denials = auditSink.Entries.Where(e => e.Message.Contains("DENIED", StringComparison.Ordinal)).ToList();

        denials.Should().ContainSingle("a single refused call must yield exactly one audit record, not zero and not two");
        var (level, message) = denials[0];
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain("test-subject", "the record must attribute the denial to the caller's subject id");
        message.Should().Contain("Delete_account", "the record must name the tool that was refused");
        message.Should().Contain("vitally:delete", "the record must state the permission the caller lacked");
        message.Should().NotContain("acc-1", "call arguments must never reach the audit log");
    }

    /// <summary>
    /// Discovery filtering is normal operation, not a denial. The SDK evaluates the policy once per
    /// tool per <c>tools/list</c> (93 evaluations, uncached), so auditing every non-success would
    /// write 37 spurious records for a reader on every list — on every client reconnect — and bury
    /// the real denials. This pins the resource-type discrimination in
    /// <c>VitallyPermissionHandler</c> that prevents it.
    /// </summary>
    [Fact]
    public async Task ReaderToolsList_ProducesNoAuditDenyRecords()
    {
        var auditSink = new CapturingLoggerProvider(typeof(AuditLogger).FullName!);

        await PostMcpAsync(noAuth: false, ["vitally:read"],
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            auditSink);

        auditSink.Entries.Where(e => e.Message.Contains("DENIED", StringComparison.Ordinal))
            .Should().BeEmpty("filtering a tool out of tools/list is not a denial and must not be audited");
    }
}
