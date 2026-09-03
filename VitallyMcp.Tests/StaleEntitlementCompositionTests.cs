using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace VitallyMcp.Tests;

/// <summary>
/// The serve-stale-on-Graph-failure path (#106), observed through a composed host rather than by
/// unit-testing the resolver.
/// </summary>
/// <remarks>
/// <para>
/// #106 shipped that path with unit coverage but it had <b>never been observed working</b>. Staging
/// cannot close the gap: it shares the managed identity and the Container Apps Environment with
/// production, so a Graph failure cannot be induced there in isolation. The cutover (#108) is where
/// the gap stops being theoretical — with the token-claim tier removed, the stale copy is the
/// <i>only</i> thing standing between a Graph outage and a total denial — so it is closed here.
/// </para>
/// <para>
/// What this adds over <see cref="GraphGroupPermissionResolverTests"/> is composition. Those tests
/// construct the resolver directly; this drives the real wiring — DI registration, the typed Graph
/// <see cref="HttpClient"/>, the singleton <c>IMemoryCache</c> that carries the retained copy between
/// requests, <see cref="ToolAuthorizer"/>, <see cref="VitallyPermissionHandler"/> and the SDK
/// authorisation filter — and reads the outcome off <c>tools/list</c>, which is what a user sees. A
/// resolver that behaved perfectly while (say) being handed a fresh cache per request would pass
/// there and fail here.
/// </para>
/// </remarks>
[Collection(IntegrationTestCollection.Name)]
public class StaleEntitlementCompositionTests
{
    private const string ReaderGroup = "71451cc9-f5df-44ee-8ed1-3acc41a911eb";
    private const string EditorGroup = "19b9d659-284c-4f93-b1c3-a6354db1027c";
    private const string AdminGroup = "70b48a20-d4b1-47dc-a132-21bc99272a86";
    private const string UserOid = "675ebdda-7590-4d79-8ec3-a2d17ab029ba";

    private const int FreshSeconds = 60;
    private const int StaleSeconds = 3600;

    [Fact]
    public async Task GraphOutage_ServesTheLastKnownGoodTier_ThenDeniesOnceItIsTooStale()
    {
        // One scenario in one host on purpose: the stale path only engages *after* a success, so the
        // three phases are not separable into independent tests — the retained copy is the state
        // that links them.
        using var harness = new Harness(memberOf: [EditorGroup]);

        var whileHealthy = await harness.ToolNamesAsync();
        whileHealthy.Should().Contain(n => n.StartsWith("Create_", StringComparison.Ordinal),
            "the editor tier is resolved from Graph while it is reachable");

        // Past the fresh window, so the next call must actually ask Graph — and Graph is now down.
        harness.Graph.Status = HttpStatusCode.ServiceUnavailable;
        harness.Clock.Advance(TimeSpan.FromSeconds(FreshSeconds + 1));

        var duringOutage = await harness.ToolNamesAsync();
        duringOutage.Should().BeEquivalentTo(whileHealthy,
            "a Graph outage must degrade to this caller's last known-good tier, not revoke it");

        // Past the stale window as well. Bounded staleness is the trade #106 made: access is
        // retained during an outage, but not indefinitely.
        harness.Clock.Advance(TimeSpan.FromSeconds(StaleSeconds + 1));

        var afterTheWindow = await harness.ToolNamesAsync();
        afterTheWindow.Should().BeEmpty(
            "once the retained copy is older than LiveGroupStaleSeconds there is nothing left to serve");
    }

    [Fact]
    public async Task GraphOutage_DeniesEvenACallerWhoseTokenClaimGrantsTheTier()
    {
        // The cutover half of the same story (#108). Before it, exhausting the stale window fell
        // through to the Auth0 post-login Action's `permissions` claim; the Action is gone, so the
        // claim is permanently absent and that tier was removed rather than left reading like a
        // working fallback. This principal carries the claim anyway — a version that still consulted
        // it would show the full tool list here.
        using var harness = new Harness(memberOf: [AdminGroup], tokenPermissions:
            ["vitally:read", "vitally:write", "vitally:delete"]);

        (await harness.ToolNamesAsync()).Should().NotBeEmpty("Graph is reachable to begin with");

        harness.Graph.Status = HttpStatusCode.ServiceUnavailable;
        harness.Clock.Advance(TimeSpan.FromSeconds(FreshSeconds + StaleSeconds + 1));

        (await harness.ToolNamesAsync()).Should().BeEmpty(
            "with the live check on, the token claim is not a source of entitlement any more");
    }

    [Fact]
    public async Task RecoveredGraph_ReplacesTheRetainedTierRatherThanCompoundingIt()
    {
        // Guards the stale path against over-reach in the other direction: a retained copy must not
        // outlive the outage that justified it. A revocation applied during the outage has to take
        // effect on the first successful lookup afterwards, not at the end of the stale window.
        using var harness = new Harness(memberOf: [AdminGroup]);

        (await harness.ToolNamesAsync()).Should().Contain(n => n.StartsWith("Delete_", StringComparison.Ordinal));

        harness.Graph.Status = HttpStatusCode.ServiceUnavailable;
        harness.Clock.Advance(TimeSpan.FromSeconds(FreshSeconds + 1));
        (await harness.ToolNamesAsync()).Should().Contain(n => n.StartsWith("Delete_", StringComparison.Ordinal),
            "still inside the stale window");

        // Graph comes back, and the user has been demoted to reader in the meantime.
        harness.Graph.Status = HttpStatusCode.OK;
        harness.Graph.MemberGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReaderGroup };
        harness.Clock.Advance(TimeSpan.FromSeconds(1));

        var afterRecovery = await harness.ToolNamesAsync();
        afterRecovery.Should().Contain(n => n.StartsWith("List_", StringComparison.Ordinal));
        afterRecovery.Should().NotContain(n => n.StartsWith("Delete_", StringComparison.Ordinal),
            "the fresh answer is authoritative the moment Graph can give one");
    }

    /// <summary>
    /// One composed host plus the two things a test needs to steer it: the clock the freshness and
    /// staleness windows are measured against, and the Graph endpoint's health. Disposing it tears
    /// the host down and restores the environment variables the composition root reads directly.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly WebApplicationFactory<Program> _baseFactory;
        private readonly WebApplicationFactory<Program> _factory;

        public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
        public GraphHandler Graph { get; }

        public Harness(string[] memberOf, string[]? tokenPermissions = null)
        {
            Graph = new GraphHandler(new HashSet<string>(memberOf, StringComparer.OrdinalIgnoreCase));

            // Process-wide, and read by Program.cs at composition time before test configuration is
            // injected — hence the collection this class belongs to. Set every one the sibling
            // classes touch, so scheduling order cannot decide the outcome.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "false");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("OAuth__Authority", "https://example.auth0.com/");
            Environment.SetEnvironmentVariable("OAuth__Audience", "https://example.test/");
            Environment.SetEnvironmentVariable("Authorization__LiveGroupCheck", "true");
            Environment.SetEnvironmentVariable("Authorization__LiveGroupCacheSeconds", FreshSeconds.ToString());
            Environment.SetEnvironmentVariable("Authorization__LiveGroupStaleSeconds", StaleSeconds.ToString());
            Environment.SetEnvironmentVariable("Authorization__ReaderGroupId", ReaderGroup);
            Environment.SetEnvironmentVariable("Authorization__EditorGroupId", EditorGroup);
            Environment.SetEnvironmentVariable("Authorization__AdminGroupId", AdminGroup);

            _baseFactory = new WebApplicationFactory<Program>();
            _factory = _baseFactory.WithWebHostBuilder(b => b.ConfigureServices(services =>
            {
                // Authenticate as a principal carrying `oid`, which is what makes ToolAuthorizer
                // take the live group path at all.
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(TestAuthHandler.SchemeName, o =>
                    {
                        o.ObjectId = UserOid;
                        o.Permissions = tokenPermissions ?? [];
                    });
                services.Configure<AuthorizationOptions>(o =>
                    o.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser().Build());

                // Registered after Program.cs's own, so these win: last registration is what
                // GetRequiredService resolves, and the named-client options are merged by name.
                services.AddSingleton<TokenCredential>(new StubTokenCredential());
                services.AddSingleton<TimeProvider>(Clock);
                services.AddHttpClient<IGroupPermissionResolver, GraphGroupPermissionResolver>()
                    .ConfigurePrimaryHttpMessageHandler(() => Graph);
            }));
        }

        /// <summary>Tool names this caller is shown, which is the authorisation outcome made visible.</summary>
        public async Task<IReadOnlyList<string>> ToolNamesAsync()
        {
            using var client = _factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

            using var response = await client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            var json = raw.Contains("data:", StringComparison.Ordinal)
                ? raw.Split('\n').First(l => l.TrimStart().StartsWith("data:", StringComparison.Ordinal))
                    .Trim()["data:".Length..].Trim()
                : raw;

            using var doc = JsonDocument.Parse(json);
            doc.RootElement.TryGetProperty("error", out var error).Should().BeFalse(
                $"tools/list must succeed even when the caller is entitled to nothing, but the server returned {error}");

            return doc.RootElement.GetProperty("result").GetProperty("tools")
                .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
        }

        public void Dispose()
        {
            // Both, deliberately: WithWebHostBuilder returns a new factory rather than mutating the
            // receiver, so disposing only the result leaks the one the constructor made.
            _factory.Dispose();
            _baseFactory.Dispose();

            foreach (var name in new[]
            {
                "OAuth__NoAuth", "Authorization__ReadOnly", "Vitally__DevelopmentApiKey", "Vitally__Region",
                "OAuth__Authority", "OAuth__Audience", "Authorization__LiveGroupCheck",
                "Authorization__LiveGroupCacheSeconds", "Authorization__LiveGroupStaleSeconds",
                "Authorization__ReaderGroupId", "Authorization__EditorGroupId", "Authorization__AdminGroupId"
            })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    /// <summary>
    /// Controllable clock. The freshness and staleness decisions are age comparisons against
    /// <see cref="TimeProvider"/>, and <see cref="Microsoft.Extensions.Caching.Memory.MemoryCache"/>
    /// expiry runs on the real clock and cannot be wound forward — so the windows are only reachable
    /// through this.
    /// </summary>
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
    /// Answers each per-group membership query. Both the membership and the health are mutable,
    /// because the scenario needs Graph to go down and come back mid-test — a handler that failed
    /// from the first call could never reach the stale path at all.
    /// </summary>
    private sealed class GraphHandler(ISet<string> memberGroupIds) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public ISet<string> MemberGroupIds { get; set; } = memberGroupIds;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            var isMember = MemberGroupIds.Any(id => uri.Contains(id, StringComparison.OrdinalIgnoreCase));

            var response = Status != HttpStatusCode.OK
                ? new HttpResponseMessage(Status) { Content = new StringContent("""{"error":"graph is down"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(isMember ? $$"""{"value":[{"id":"{{UserOid}}"}]}""" : """{"value":[]}""")
                };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _responses)
                {
                    response.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
