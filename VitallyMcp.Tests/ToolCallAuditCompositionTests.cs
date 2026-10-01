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
        // The scenario above gives every check in a call the SAME answer, so it cannot tell "the
        // admitting check's staleness reached the record" from "whichever check ran last did" — or
        // from the handler's authorizer writing into a different scope's context than the one the
        // filter reads. This splits them inside one call: Graph answers the first check, then goes
        // down while the fresh window lapses, so every later check is served the retained copy. The
        // record must say False. Last-write-wins, a scope split, or a dropped first write each
        // record True.
        using var harness = new Harness(TwoOrganisations);
        harness.Graph.AfterRespond = () =>
        {
            harness.Graph.AfterRespond = null;
            harness.Graph.Status = HttpStatusCode.ServiceUnavailable;
            harness.Clock.Advance(TimeSpan.FromSeconds(FreshSeconds + 1));
        };

        var result = await harness.CallToolAsync("List_organizations");

        // First, so a broken premise fails with its real cause rather than as a misleading denial
        // below. Each lookup is ONE request, because only ReaderGroup is configured. And there are
        // three lookups, not two — observed, not assumed: SDK 2.2.0 evaluates the [Authorize] policy
        // twice per tools/call (ConfigureCallToolFilter, then ConfigureOrdinaryCallToolFilter) before
        // the VitallyService backstop runs. So only the FIRST admission check is answered by Graph;
        // the second and the backstop are both served the retained copy.
        harness.Graph.Requests.Should().Be(3,
            "two SDK admission checks and one backstop, one Graph request each");
        harness.Graph.FailedResponses.Should().Be(2,
            "only the first check was answered, or the split this test relies on did not happen");
        result.Should().NotContain("\"error\"", "the later checks are admitted on the retained tier");
        harness.AuditRecords.Should()
            .ContainSingle(e => e.Message.Contains("called List_organizations", StringComparison.Ordinal))
            .Subject.Message.Should().Contain("tierStale=False",
                "the record documents the first check that admitted the call, and Graph confirmed that "
                + "one — so the caller was entitled per Graph at the moment of the call");
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

        /// <summary>The exception attached to each SDK record, index-aligned with <see cref="SdkRecords"/>.</summary>
        public IReadOnlyList<Exception?> SdkExceptions => _sdk.Exceptions;

        /// <summary>The composed host's services — for resolving what the real wiring produced.</summary>
        public IServiceProvider Services => _factory.Services;

        public IReadOnlyList<(LogLevel Level, string Message)> AuditRecords => _audit.Entries;

        /// <summary>Records from the tool-call failure log (#94).</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> FailureRecords => _failures.Entries;

        /// <summary>Records from <see cref="VitallyService"/> itself — the upstream-call failure log.</summary>
        public IReadOnlyList<(LogLevel Level, string Message)> ServiceRecords => _service.Entries;

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
            Environment.SetEnvironmentVariable("Authorization__LiveGroupCacheSeconds", FreshSeconds.ToString());
            Environment.SetEnvironmentVariable("Authorization__LiveGroupStaleSeconds", StaleSeconds.ToString());
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
                    services.AddSingleton<TimeProvider>(Clock);
                    services.AddHttpClient<IGroupPermissionResolver, GraphGroupPermissionResolver>()
                        .ConfigurePrimaryHttpMessageHandler(() => Graph);

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

        /// <summary>Every request, answered or refused.</summary>
        public int Requests { get; private set; }

        protected override HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Requests++;
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

        // Asserting the Warning was written, not only that nothing bad was: without it this would pass
        // with the filter never running at all.
        harness.FailureRecords.Should().ContainSingle().Subject.Should().Match<(LogLevel Level, string Message)>(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("unrecognised", StringComparison.Ordinal),
            "naming a tool that does not exist is a client error, not a server fault");
        harness.FailureRecords.Should().NotContain(e => e.Message.Contains("alice_at_example", StringComparison.Ordinal));
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
            "each absorbed section is recorded, so a failure the summary hid is still visible")
            .And.OnlyContain(e => e.Message.Contains("catalogue unavailable", StringComparison.Ordinal),
                "the catalogue fetch failed, which is a different fix from a renamed object");
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
        // Exactly one, with the exception attached: CLAUDE.md's Error-lines table and its warning that
        // the SDK's line carries the message both rest on this.
        var sdkErrors = harness.SdkRecords.Select((e, i) => (e.Level, Exception: harness.SdkExceptions[i]))
            .Where(e => e.Level == LogLevel.Error).ToList();
        sdkErrors.Should().ContainSingle("the SDK logs an unhandled tool exception itself, once")
            .Which.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ASuccessfulCall_LogsNoFailure()
    {
        using var harness = new Harness(TwoOrganisations);

        await harness.CallToolAsync("List_organizations");

        harness.FailureRecords.Should().BeEmpty();
    }

    [Fact]
    public async Task TheComposedHost_InjectsTheCounters_IntoTheComponentsThatRecordThem()
    {
        // #94. VitallyMetrics reaches every consumer as an OPTIONAL constructor parameter, so a
        // registration that never resolved would leave each one with null and record nothing — and
        // every unit test, which injects it by hand, would still pass. Asserted through the real
        // wiring: a pager that never runs out of pages must be counted as truncated.
        using var harness = new Harness(
            """{"results":[{"id":"org-1","name":"Acme"}],"next":"more"}""");
        using var capture = new MetricCapture(
            harness.Services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());

        await harness.CallToolAsync("List_organizations", """{"nameContains":"Acme"}""");

        capture.Total("vitally.autopager.truncations", ("resource", "organizations")).Should().Be(1);
    }

    [Fact]
    public async Task TheMcpSdk_PublishesTheSourcesWeRegister_AndNoSpanCarriesAToolArgument()
    {
        // Two things, both about the SDK's telemetry that Program.cs registers with the exporter.
        //
        // 1. The names are real. A source registered under a name nothing publishes is accepted
        //    silently and captures nothing, and the names come from the SDK rather than from us.
        //
        // 2. The PII gate. Tool arguments are permitted in the AUDIT record (AppEvents, under its
        //    access control), but spans go to AppRequests / AppDependencies, a different store with
        //    its own retention. A search term in a span attribute would put customer data there.
        const string sentinel = "argumentsentinelvalue";
        var activities = new List<System.Diagnostics.Activity>();
        using var activityListener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (activities) activities.Add(a); },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(activityListener);

        var meterNames = new HashSet<string>(StringComparer.Ordinal);
        using var meterListener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, _) => { lock (meterNames) meterNames.Add(instrument.Meter.Name); },
        };
        meterListener.Start();

        using var harness = new Harness(TwoOrganisations);
        await harness.CallToolAsync("List_organizations", "{\"nameContains\":\"" + sentinel + "\"}");

        List<System.Diagnostics.Activity> captured;
        lock (activities) captured = [.. activities];

        captured.Select(a => a.Source.Name).Should().Contain(TelemetrySources.McpActivitySource,
            "Program.cs registers this name; observed sources: "
            + string.Join(", ", captured.Select(a => a.Source.Name).Distinct()));
        lock (meterNames)
        {
            meterNames.Should().Contain(TelemetrySources.McpMeter,
                "Program.cs registers this meter name; published meters: " + string.Join(", ", meterNames));
        }

        foreach (var activity in captured)
        {
            activity.DisplayName.Should().NotContain(sentinel);
            activity.TagObjects.Should().NotContain(t => Carries(t.Value, sentinel),
                $"span '{activity.DisplayName}' must not carry a tool argument");
            activity.Events.SelectMany(e => e.Tags).Should().NotContain(
                t => Carries(t.Value, sentinel));
        }
    }

    // A method rather than an inline `?.`: FluentAssertions compiles these predicates as expression
    // trees, which cannot contain a null-propagating operator.
    private static bool Carries(object? value, string sentinel) =>
        value is not null && (value.ToString() ?? "").Contains(sentinel, StringComparison.Ordinal);
}
