using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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

        private readonly CapturingLoggerProvider _failures = new(ToolCallFailureLog.Category);
        private readonly CapturingLoggerProvider _service = new("VitallyMcp.VitallyService");
        private readonly CapturingLoggerProvider _sdk = new("ModelContextProtocol", matchPrefix: true);

        /// <summary>Records from the MCP SDK itself, whichever of its categories logged them.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> SdkRecords => _sdk.Entries;

        public IReadOnlyList<(LogLevel Level, string Message)> AuditRecords => _audit.Entries;

        /// <summary>Records from the tool-call failure log (#94).</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> FailureRecords => _failures.Entries;

        /// <summary>Records from <see cref="VitallyService"/> itself — the upstream-call failure log.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> ServiceRecords => _service.Entries;

        public Harness(
            string vitallyBody,
            HttpStatusCode vitallyStatus = HttpStatusCode.OK,
            bool sabotageAudit = false,
            Func<Exception>? vitallyThrows = null,
            string? failPathContaining = null)
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
                    l.AddProvider(_failures);
                    l.AddProvider(_service);
                    l.AddProvider(_sdk);
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
                        .ConfigurePrimaryHttpMessageHandler(() => new VitallyHandler(vitallyBody, vitallyStatus, vitallyThrows, failPathContaining));
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

        public async Task<string> CallToolAsync(string toolName, string argumentsJson = "{}")
        {
            using var client = _factory.CreateClient();
            var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\""
                + toolName + "\",\"arguments\":" + argumentsJson + "}}";
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
            _failures.Dispose();
            _service.Dispose();
            _sdk.Dispose();

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

    private sealed class VitallyHandler(
        string body, HttpStatusCode status, Func<Exception>? throws = null, string? failPathContaining = null)
        : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request)
        {
            if (throws is not null)
            {
                throw throws();
            }

            // Fails one upstream path only, for the composite tools that absorb a failed sub-call.
            if (failPathContaining is not null
                && request.RequestUri!.AbsolutePath.Contains(failPathContaining, StringComparison.Ordinal))
            {
                return new(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"message\":\"sub-call exploded\"}", Encoding.UTF8, "application/json")
                };
            }

            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
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

    [Fact]
    public async Task TheUpstreamRecordsCarryTheSameCorrelationIdAsTheToolCall()
    {
        // The tool-call record is the primary evidence and the upstream records corroborate it — but
        // only if they can be joined. A composite tool makes four upstream calls and the pager up to
        // ten, so without a shared id there is no telling which upstream calls belonged to which
        // tool call, and the corroboration is worthless.
        using var harness = new Harness(TwoOrganisations);

        await harness.CallToolAsync("List_organizations");

        var toolCall = harness.AuditRecords
            .Single(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal)).Message;
        var correlation = Regex.Match(toolCall, @"correlation=([0-9a-f]{32})").Groups[1].Value;
        correlation.Should().NotBeEmpty("the tool-call record carries a correlation id");

        harness.AuditRecords
            .Where(e => e.Message.Contains(" GET ", StringComparison.Ordinal))
            .Should().NotBeEmpty("the upstream call is recorded too")
            .And.OnlyContain(e => e.Message.Contains("correlation=" + correlation, StringComparison.Ordinal),
                "every upstream record joins to the tool call that caused it");
    }

    [Fact]
    public async Task AnUpstreamFailure_IsLoggedAtError_ByTheFilterAndTheService_JoinedToTheAuditRecord()
    {
        // #94: the error-surfacing filter used to return the failure to the client without logging
        // it, so a Vitally outage left no server-side trace. Asserted through a composed host because
        // the filter only has a correlation id if it resolves the SAME scoped context the tool wrote to.
        using var harness = new Harness(
            "{\"message\":\"upstream exploded\"}", HttpStatusCode.InternalServerError);

        var result = await harness.CallToolAsync("List_organizations");
        result.Should().Contain("upstream exploded", "the client still sees the real reason");

        var correlation = Regex.Match(
            harness.AuditRecords.Single(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal)).Message,
            @"correlation=([0-9a-f]{32})").Groups[1].Value;
        correlation.Should().NotBeEmpty("the tool-call audit record carries a correlation id");

        var failure = harness.FailureRecords.Should().ContainSingle().Subject;
        failure.Level.Should().Be(LogLevel.Error);
        failure.Message.Should().Contain("List_organizations").And.Contain("HttpRequestException")
            .And.Contain("500").And.Contain(correlation);
        failure.Message.Should().NotContain("upstream exploded", "the body can carry customer data");

        var upstream = harness.ServiceRecords.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        upstream.Message.Should().Contain("500").And.Contain(correlation);
        upstream.Message.Should().NotContain("upstream exploded");
    }

    [Fact]
    public async Task ATransportFailure_IsLoggedAtError()
    {
        // No response at all, so SendAsync's non-2xx record never fires — the filter is the only
        // place this failure can be seen.
        using var harness = new Harness(TwoOrganisations,
            vitallyThrows: () => new HttpRequestException(HttpRequestError.ConnectionError, "connection refused"));

        await harness.CallToolAsync("List_organizations");

        var failure = harness.FailureRecords.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject.Message;
        failure.Should().Contain("HttpRequestException").And.Contain("status=none");
        failure.Should().Contain("ConnectionError",
            "HttpRequestError is what tells DNS from a refused connection from TLS, and carries no customer data");
        harness.ServiceRecords.Should().BeEmpty("with no response, SendAsync's non-2xx record cannot fire");
    }

    [Fact]
    public async Task ACaller4xx_IsAWarning_NotAnError()
    {
        // A wrong id from the model is routine and is the caller's input, not a fault — logging it at
        // Error would bury a real outage in noise once #159 alerts on that level.
        using var harness = new Harness("{\"message\":\"not found\"}", HttpStatusCode.NotFound);

        await harness.CallToolAsync("List_organizations");

        harness.FailureRecords.Should().ContainSingle().Subject.Level.Should().Be(LogLevel.Warning);
        harness.ServiceRecords.Should().ContainSingle().Subject.Level.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task AnUnknownTool_NeverPutsTheCallersNameOnTheLog_AndIsNotAnError()
    {
        // The name in a tools/call is caller-controlled text, and this category stays on the console,
        // which the data map declares customer-data-free. Same rule as the audit breadcrumb.
        using var harness = new Harness(TwoOrganisations);

        await harness.CallToolAsync("alice_at_example");

        harness.FailureRecords.Should().NotContain(e => e.Message.Contains("alice_at_example", StringComparison.Ordinal));
        harness.FailureRecords.Should().NotContain(e => e.Level >= LogLevel.Error,
            "naming a tool that does not exist is a client error, not a server fault");
    }

    [Fact]
    public async Task AnAbsorbedSubCallFailure_InTheSummary_IsLoggedAlthoughTheToolSucceeds()
    {
        // Get_organization_summary turns a failed section into `{error: ...}` inside a successful
        // result, so the tool-call log never fires and the audit record says ok. These lines are the
        // only server-side trace of it — which is why the upstream failure is logged per upstream
        // call, not per tool call.
        using var harness = new Harness(
            "{\"id\":\"org-1\",\"name\":\"Acme\",\"results\":[]}", failPathContaining: "/resources/customObjects");

        var result = await harness.CallToolAsync("Get_organization_summary", "{\"organizationId\":\"org-1\"}");
        // Bare word: the summary is JSON-escaped inside the tool result's text content.
        result.Should().Contain("goals", "the summary still returns");
        result.Should().NotContain("\"isError\":true", "the tool itself succeeded");

        harness.FailureRecords.Should().BeEmpty("the tool itself succeeded");
        harness.ServiceRecords.Should().Contain(e => e.Level == LogLevel.Error
            && e.Message.Contains("/resources/customObjects", StringComparison.Ordinal));
        harness.ServiceRecords.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(2,
            "each absorbed section is recorded, so a failure the summary hid is still visible");
    }

    [Fact]
    public async Task AnUnexpectedException_IsLoggedAtError_AndStillLeftToTheSdk()
    {
        // Not a surfaceable type, so the SDK keeps its generic message. The SDK ALSO logs this at Error
        // itself, with the exception attached — pinned below, because the docs rest on it: ours adds
        // the correlation id that joins it to the audit record, and the SDK's carries the stack trace.
        using var harness = new Harness(TwoOrganisations,
            vitallyThrows: () => new InvalidOperationException("internal detail"));

        var result = await harness.CallToolAsync("List_organizations");
        result.Should().NotContain("internal detail", "unexpected detail is never surfaced to the client");

        var failure = harness.FailureRecords.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        failure.Message.Should().Contain("InvalidOperationException");
        failure.Message.Should().NotContain("internal detail");
        harness.SdkRecords.Should().Contain(e => e.Level == LogLevel.Error,
            "the SDK logs an unhandled tool exception itself — CLAUDE.md counts on that");
    }

    [Fact]
    public async Task ASuccessfulCall_LogsNoFailure()
    {
        using var harness = new Harness(TwoOrganisations);

        await harness.CallToolAsync("List_organizations");

        harness.FailureRecords.Should().BeEmpty();
    }
}
