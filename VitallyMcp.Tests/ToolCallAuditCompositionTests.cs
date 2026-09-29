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

    private const int FreshSeconds = 60;
    private const int StaleSeconds = 3600;

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

    [Fact]
    public async Task TheRecordSaysWhetherTheTierWasServedStale_AcrossAGraphOutage()
    {
        // #161, through the real wiring. The unit tests prove each hop — resolver reports it, the
        // authorizer passes it on, the context keeps it, the logger renders it — but a record only
        // carries the truth if the SAME answer the admission decision used is the one that lands in
        // the scoped context the filter reads. One scenario in one host, because the stale path only
        // engages after a success: the retained copy is the state linking the three phases.
        using var harness = new Harness(TwoOrganisations);

        await harness.CallToolAsync("List_organizations");

        // Past the fresh window, so this call must ask Graph — and Graph is down.
        harness.Graph.Status = HttpStatusCode.ServiceUnavailable;
        harness.Clock.Advance(TimeSpan.FromSeconds(FreshSeconds + 1));
        var duringOutage = await harness.CallToolAsync("List_organizations");
        duringOutage.Should().NotContain("\"error\"", "the retained tier still admits the call");

        // Past the stale window as well: nothing left to serve, so the call must be refused.
        harness.Clock.Advance(TimeSpan.FromSeconds(StaleSeconds + 1));
        await harness.CallToolAsync("List_organizations");

        var calls = harness.AuditRecords
            .Where(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal))
            .Select(e => e.Message)
            .ToList();
        calls.Should().HaveCount(2, "the two admitted calls each leave a record; the refused one does not");

        calls[0].Should().Contain("tier=vitally:read").And.Contain("tierStale=False",
            "Graph answered, so the resolver can say the tier was fresh — a checked claim");
        calls[1].Should().Contain("tier=vitally:read").And.Contain("tierStale=True",
            "the tier came from the retained copy, and the record must say so");
        calls.Should().NotContain(m => m.Contains("tierStale=unknown", StringComparison.Ordinal),
            "on the live path the resolver always knows which branch it took");

        harness.AuditRecords.Should().Contain(
            e => e.Message.Contains("DENIED tools/call List_organizations", StringComparison.Ordinal),
            "null from the resolver still means deny — the widened return must not have softened it");
    }

    [Fact]
    public async Task TheRecordKeepsTheAdmissionDecisionsStaleness_WhenALaterCheckInTheSameCallIsServedStale()
    {
        // The scenario above gives the admission check and the VitallyService backstop the SAME
        // answer in every phase, so it cannot tell "the admission decision's staleness reached the
        // record" from "whichever check ran last did" — or from the handler's authorizer writing into
        // a different scope's context than the one the filter reads. This splits them inside one call:
        // Graph answers the admission lookup, then goes down while the fresh window lapses, so the
        // backstop is served the retained copy. The record must say False — the admission tier was
        // confirmed. Last-write-wins, a scope split, or a dropped admission write each record True.
        using var harness = new Harness(TwoOrganisations);
        harness.Graph.AfterRespond = () =>
        {
            harness.Graph.AfterRespond = null;
            harness.Graph.Status = HttpStatusCode.ServiceUnavailable;
            harness.Clock.Advance(TimeSpan.FromSeconds(FreshSeconds + 1));
        };

        var result = await harness.CallToolAsync("List_organizations");
        result.Should().NotContain("\"error\"", "the backstop is admitted on the retained tier");

        harness.Graph.FailedResponses.Should().BeGreaterThan(0,
            "the backstop must actually have asked Graph and been refused, or this proves nothing");
        harness.AuditRecords.Should()
            .ContainSingle(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal))
            .Subject.Message.Should().Contain("tierStale=False",
                "the record documents the decision that admitted the call, and Graph confirmed that one");
    }

    private sealed class Harness : IDisposable
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly WebApplicationFactory<Program> _factory;
        private readonly CapturingLoggerProvider _audit = new("VitallyMcp.AuditLogger");

        public IReadOnlyList<(LogLevel Level, string Message)> AuditRecords => _audit.Entries;

        /// <summary>
        /// The clock the resolver's freshness and staleness windows are measured against. Shared with
        /// the host rather than left on the system clock so a test can walk a caller through a Graph
        /// outage — <c>IMemoryCache</c> expiry cannot be wound forward, the same constraint
        /// <see cref="StaleEntitlementCompositionTests"/> works around this way.
        /// </summary>
        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        /// <summary>One instance for the host's lifetime, so a test can take Graph down mid-scenario.</summary>
        public GraphHandler Graph { get; } = new(ReaderGroup);

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
            Environment.SetEnvironmentVariable("Authorization__LiveGroupCacheSeconds", FreshSeconds.ToString());
            Environment.SetEnvironmentVariable("Authorization__LiveGroupStaleSeconds", StaleSeconds.ToString());
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
                    services.AddSingleton<TimeProvider>(Clock);
                    services.AddHttpClient<IGroupPermissionResolver, GraphGroupPermissionResolver>()
                        .ConfigurePrimaryHttpMessageHandler(() => Graph);

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
            // Handed to the host as an instance, so the host does not own it. Idempotent if the
            // client factory disposed it on handler rotation first.
            Graph.Dispose();

            foreach (var name in new[]
            {
                "OAuth__NoAuth", "Authorization__ReadOnly", "Vitally__DevelopmentApiKey", "Vitally__Region",
                "OAuth__Authority", "OAuth__Audience", "Authorization__LiveGroupCheck",
                "Authorization__ReaderGroupId", "Authorization__LiveGroupCacheSeconds",
                "Authorization__LiveGroupStaleSeconds", "Audit__Enabled", "Audit__IncludeReads"
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

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
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
        /// <summary>Mutable because the stale path only engages after an earlier success.</summary>
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        /// <summary>
        /// Runs after each response is produced. Lets a test change the world <i>between</i> two
        /// lookups inside one tool call, which is the only way to give the admission check and the
        /// backstop different answers.
        /// </summary>
        public Action? AfterRespond { get; set; }

        /// <summary>How many lookups Graph refused — evidence the stale path was actually taken.</summary>
        public int FailedResponses { get; private set; }

        protected override HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var response = Answer(request);
            AfterRespond?.Invoke();
            return response;
        }

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            if (Status != HttpStatusCode.OK)
            {
                FailedResponses++;
                return new HttpResponseMessage(Status)
                {
                    Content = new StringContent("{\"error\":\"graph is down\"}", Encoding.UTF8, "application/json")
                };
            }

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
}
