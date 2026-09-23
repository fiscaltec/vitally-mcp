using System.Net;
using System.Text;
using Azure.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VitallyMcp.Tests;

/// <summary>
/// The tool-call audit record (#147) observed through a composed host, not by unit-testing the
/// pieces.
/// </summary>
/// <remarks>
/// This suite exists for the reason <see cref="StaleEntitlementCompositionTests"/> does: #106 shipped
/// a path with unit coverage alone that had never been seen working. The pieces here each have their
/// own tests, but a record only exists if the scoped context registered in <c>Program.cs</c> is the
/// <i>same instance</i> the tool's <see cref="VitallyService"/> wrote into and the filter later read.
/// A context resolved per-dependency rather than per-request would pass every unit test and record
/// nothing at all.
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class ToolCallAuditCompositionTests
{
    private const string ReaderGroup = "71451cc9-f5df-44ee-8ed1-3acc41a911eb";
    private const string UserOid = "675ebdda-7590-4d79-8ec3-a2d17ab029ba";

    private const string TwoOrganisations =
        "{\"results\":[{\"id\":\"org-1\",\"name\":\"Acme\"},{\"id\":\"org-2\",\"name\":\"Globex\"}]}";

    [Fact]
    public async Task AToolCall_LeavesARecordNamingTheUser_TheTool_AndTheCustomersItTouched()
    {
        // `List_organizations` is deliberately the tool under test: an unscoped list is the case the
        // upstream record cannot answer, because the customers appear only in the response body.
        using var harness = new Harness(TwoOrganisations);

        var result = await harness.CallToolAsync("List_organizations");
        result.Should().NotContain("\"error\"", "the call itself must succeed");

        var record = harness.AuditRecords.Should()
            .ContainSingle(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal),
                "one record per tool call")
            .Subject.Message;

        record.Should().Contain(UserOid, "this user");
        record.Should().Contain("org-1").And.Contain("org-2", "these customers");
        record.Should().Contain("tier=vitally:read", "the tier the decision was actually made against");
        record.Should().Contain("outcome=ok");
    }

    private sealed class Harness : IDisposable
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly WebApplicationFactory<Program> _factory;
        private readonly CapturingLoggerProvider _audit = new("VitallyMcp.AuditLogger");

        public IReadOnlyList<(LogLevel Level, string Message)> AuditRecords => _audit.Entries;

        public Harness(
            string vitallyBody,
            HttpStatusCode vitallyStatus = HttpStatusCode.OK,
            bool sabotageAudit = false)
        {
            // Process-wide, and read by Program.cs at composition time before test configuration is
            // injected — hence the collection this class belongs to.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "false");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("OAuth__Authority", "https://example-issuer.test/");
            Environment.SetEnvironmentVariable("OAuth__Audience", "https://example.test/");
            Environment.SetEnvironmentVariable("Authorization__LiveGroupCheck", "true");
            Environment.SetEnvironmentVariable("Authorization__ReaderGroupId", ReaderGroup);
            Environment.SetEnvironmentVariable("Audit__Enabled", "true");
            Environment.SetEnvironmentVariable("Audit__IncludeReads", "true");

            _baseFactory = new WebApplicationFactory<Program>();
            _factory = _baseFactory.WithWebHostBuilder(b => b
                .ConfigureLogging(l =>
                {
                    l.AddProvider(_audit);
                    if (sabotageAudit)
                    {
                        l.AddProvider(new ThrowingLoggerProvider("VitallyMcp.AuditLogger"));
                    }
                })
                .ConfigureServices(services =>
                {
                    services.AddAuthentication(TestAuthHandler.SchemeName)
                        .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                            TestAuthHandler.SchemeName, o => o.ObjectId = UserOid);
                    services.Configure<AuthorizationOptions>(o =>
                        o.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                            .RequireAuthenticatedUser().Build());

                    services.AddSingleton<TokenCredential>(new StubTokenCredential());
                    services.AddHttpClient<IGroupPermissionResolver, GraphGroupPermissionResolver>()
                        .ConfigurePrimaryHttpMessageHandler(() => new GraphHandler(ReaderGroup));

                    // Stub the upstream at the *primary* handler so the real VitallyService
                    // pipeline — the rate-limit handler included — still runs in front of it.
                    services.AddHttpClient<VitallyService>()
                        .ConfigurePrimaryHttpMessageHandler(() => new VitallyHandler(vitallyBody, vitallyStatus));
                }));
        }

        /// <summary>
        /// A 2026-07-28 caller. That revision removed the <c>initialize</c> handshake, so in stateless
        /// mode the client's identity arrives in per-request <c>_meta</c> and nowhere else — which is
        /// why this shape exists rather than reusing the legacy path above. The server enforces the
        /// full contract, so the header, the protocol version and the capabilities object are all
        /// required together.
        /// </summary>
        public async Task<string> CallToolWithClientInfoAsync(string toolName, string clientName)
        {
            using var client = _factory.CreateClient();
            var body =
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{"
                + "\"name\":\"" + toolName + "\",\"arguments\":{},\"_meta\":{"
                + "\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\","
                + "\"io.modelcontextprotocol/clientCapabilities\":{},"
                + "\"io.modelcontextprotocol/clientInfo\":{\"name\":\"" + clientName + "\",\"version\":\"0.0.1\"}"
                + "}}}";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
            request.Headers.TryAddWithoutValidation("Mcp-Method", "tools/call");
            // A fourth requirement alongside the three CLAUDE.md lists, and undocumented there: for
            // tools/call the SDK also demands the tool name in a header, rejecting its absence with
            // -32020 "Missing required Mcp-Name header."
            request.Headers.TryAddWithoutValidation("Mcp-Name", toolName);

            using var response = await client.SendAsync(request);
            return Unwrap(await response.Content.ReadAsStringAsync());
        }

        public async Task<string> CallToolAsync(string toolName)
        {
            using var client = _factory.CreateClient();
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
                + toolName + "\",\"arguments\":{}}}";
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var response = await client.SendAsync(request);
            return Unwrap(await response.Content.ReadAsStringAsync());
        }

        /// <summary>Pulls the JSON payload out of an SSE frame when the server answers with one.</summary>
        private static string Unwrap(string raw) =>
            raw.Contains("data:", StringComparison.Ordinal)
                ? raw.Split('\n')
                    .First(l => l.TrimStart().StartsWith("data:", StringComparison.Ordinal))
                    .Trim()["data:".Length..]
                    .Trim()
                : raw;

        public void Dispose()
        {
            // Both, deliberately: WithWebHostBuilder returns a new factory rather than mutating the
            // receiver, so disposing only the result leaks the one the constructor made.
            _factory.Dispose();
            _baseFactory.Dispose();
            _audit.Dispose();

            foreach (var name in new[]
            {
                "OAuth__NoAuth", "Authorization__ReadOnly", "Vitally__DevelopmentApiKey", "Vitally__Region",
                "OAuth__Authority", "OAuth__Audience", "Authorization__LiveGroupCheck",
                "Authorization__ReaderGroupId", "Audit__Enabled", "Audit__IncludeReads"
            })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    /// <summary>
    /// A logger that throws for one category, standing in for a telemetry sink that is refusing
    /// writes — the failure mode the audit trail must absorb rather than propagate.
    /// </summary>
    private sealed class ThrowingLoggerProvider(string categoryName) : ILoggerProvider
    {
        public ILogger CreateLogger(string category) =>
            category == categoryName
                ? new ThrowingLogger()
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose() { }

        private sealed class ThrowingLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                throw new InvalidOperationException("audit sink unavailable");
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("test-graph-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>
    /// Answers the per-group <c>transitiveMembers</c> query, reporting membership of one group only —
    /// so the caller resolves to exactly the reader tier.
    /// </summary>
    private sealed class GraphHandler(string memberGroupId) : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var isMember = request.RequestUri!.ToString()
                .Contains(memberGroupId, StringComparison.OrdinalIgnoreCase);
            var body = isMember ? "{\"value\":[{\"id\":\"" + UserOid + "\"}]}" : "{\"value\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class VitallyHandler(string body, HttpStatusCode status) : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// Keeps every response it hands out and disposes them with itself. A handler cannot dispose a
    /// response it is returning, so without this each one leaks — which is what CodeQL flags, and the
    /// same shape <see cref="StaleEntitlementCompositionTests"/> solves this way.
    /// </summary>
    private abstract class RecordingHandler : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _issued = [];

        protected abstract HttpResponseMessage Respond(HttpRequestMessage request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = Respond(request);
            _issued.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _issued)
                {
                    response.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task AFailedToolCall_StillLeavesARecord()
    {
        // The filter has to survive the error-surfacing filter converting an exception into an error
        // result. A trail that recorded only successes could not show an attempted access that
        // failed — which is exactly the sort of event an access record is kept for.
        using var harness = new Harness(
            "{\"message\":\"upstream exploded\"}", HttpStatusCode.InternalServerError);

        await harness.CallToolAsync("List_organizations");

        var record = harness.AuditRecords.Should()
            .ContainSingle(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal))
            .Subject.Message;

        record.Should().Contain("outcome=error");
        record.Should().Contain(UserOid, "a failed call is still attributable");
        record.Should().NotContain("upstream exploded",
            "the upstream body never enters the audit record — it can carry customer data");
    }

    [Fact]
    public async Task AToolCall_RecordsWhichMcpClientMadeIt()
    {
        // Stateless mode has no `initialize` handshake to read clientInfo from — 2026-07-28 removed
        // it — so the client's identity arrives in per-request `_meta` and must be read per call.
        // Without this the trail cannot tell Claude Desktop from VS Code from a script.
        using var harness = new Harness(TwoOrganisations);

        var response = await harness.CallToolWithClientInfoAsync("List_organizations", "smoke-client");
        response.Should().NotContain("\"error\"", "the 2026-07-28 request shape must be accepted");

        harness.AuditRecords.Should()
            .ContainSingle(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal))
            .Subject.Message.Should().Contain("client=smoke-client");
    }

    [Fact]
    public async Task AFailingAuditSink_DoesNotFailTheToolCall()
    {
        // #147's contract: an audit write must never be the reason a call fails. The record is
        // emitted from a `finally`, so an exception there would replace a perfectly good result —
        // and a telemetry sink refusing writes is exactly the sort of thing that happens during the
        // incident you most want the trail for. Losing the record is bad; losing the user's call as
        // well is worse, and inexplicable from the client side.
        using var harness = new Harness(TwoOrganisations, sabotageAudit: true);

        var result = await harness.CallToolAsync("List_organizations");

        result.Should().NotContain("\"error\"", "the tool call survives an audit sink that throws");
        result.Should().Contain("org-1", "and still returns its data");
    }
}
