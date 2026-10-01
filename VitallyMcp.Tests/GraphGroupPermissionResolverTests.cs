using System.Net;
using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

/// <summary>
/// Tests for <see cref="GraphGroupPermissionResolver"/>. Drives a recording
/// <see cref="HttpMessageHandler"/> that answers each per-group membership query, so the tests can
/// assert both the Graph relationship used (<c>/transitiveMembers</c>, which expands nested groups)
/// and the tier mapping / fail-degraded behaviour.
/// </summary>
public class GraphGroupPermissionResolverTests : IDisposable
{
    private const string ReaderGroup = "71451cc9-f5df-44ee-8ed1-3acc41a911eb";
    private const string EditorGroup = "19b9d659-284c-4f93-b1c3-a6354db1027c";
    private const string AdminGroup = "70b48a20-d4b1-47dc-a132-21bc99272a86";
    private const string UserOid = "675ebdda-7590-4d79-8ec3-a2d17ab029ba";
    private const string OtherUserOid = "9f2c3f1e-1111-4222-8333-444455556666";
    private static readonly DateTimeOffset ClockStart = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    // Disposed at the end of each test so the HttpClient (and its inner RecordingHandler, and every
    // HttpResponseMessage that handler created) is cleaned up deterministically rather than by the GC.
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Minimal controllable clock. Hand-rolled rather than taking a dependency on
    /// Microsoft.Extensions.TimeProvider.Testing for one overridden method, matching the other stubs
    /// in this project. Needed because the freshness and staleness decisions are age comparisons
    /// against <see cref="TimeProvider"/> — <see cref="MemoryCache"/> expiry runs on the real clock
    /// and cannot be wound forward.
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
    /// Answers each group-membership query: a group whose id is in <paramref name="memberGroupIds"/>
    /// returns a one-element member array (the user matched), all others return an empty array. When
    /// <c>status</c> is non-success, every call fails (drives the fail-degraded path). Records every
    /// requested URI so tests can assert the relationship and the call count. Every response it
    /// creates is retained and disposed on <see cref="Dispose(bool)"/> (invoked when the owning
    /// HttpClient is disposed), mirroring the QueueingHandler in VitallyRateLimitHandlerTests.
    /// </summary>
    private sealed class RecordingHandler(ISet<string> memberGroupIds, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        /// <summary>
        /// Invoked on every request, before the response is produced. Lets a test make the Graph call
        /// itself consume time — which is the only way to reach the case where a lookup begins inside
        /// the stale window and finishes outside it.
        /// </summary>
        public Action? OnRequest { get; set; }

        private readonly List<HttpResponseMessage> _responses = [];
        public List<string> RequestedUris { get; } = [];

        /// <summary>
        /// Mutable so a single test can take Graph down (or bring it back) part-way through. That is
        /// essential rather than convenient: the stale path only engages <i>after</i> an earlier
        /// success, so it cannot be reached by a handler that fails from the first call.
        /// </summary>
        public HttpStatusCode Status { get; set; } = status;

        /// <summary>Mutable so a recovered Graph can answer with a different tier than the stale copy.</summary>
        public ISet<string> MemberGroupIds { get; set; } = memberGroupIds;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);
            OnRequest?.Invoke();

            var response = Status != HttpStatusCode.OK
                ? new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":\"boom\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MembershipBody(uri)) };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        private string MembershipBody(string uri)
        {
            var isMember = MemberGroupIds.Any(id => uri.Contains(id, StringComparison.OrdinalIgnoreCase));
            return isMember ? "{\"value\":[{\"id\":\"" + UserOid + "\"}]}" : "{\"value\":[]}";
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

    private GraphGroupPermissionResolver Build(
        RecordingHandler handler,
        IMemoryCache? cache = null,
        TimeProvider? timeProvider = null,
        ILogger<GraphGroupPermissionResolver>? logger = null,
        int staleSeconds = 3600,
        VitallyMetrics? metrics = null)
    {
        var options = new ToolAuthorizationOptions
        {
            Enabled = true,
            LiveGroupCheck = true,
            LiveGroupStaleSeconds = staleSeconds,
            ReaderGroupId = ReaderGroup,
            EditorGroupId = EditorGroup,
            AdminGroupId = AdminGroup,
        };
        var client = new HttpClient(handler);
        _clients.Add(client);
        return new GraphGroupPermissionResolver(
            client,
            new StubTokenCredential(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options),
            logger ?? NullLogger<GraphGroupPermissionResolver>.Instance,
            timeProvider,
            metrics);
    }

    [Fact]
    public async Task CountsAMiss_WhenItAsksGraph_AndAHit_InsideTheFreshWindow()
    {
        // #94: the group-membership cache is what keeps Graph off the hot path, and its hit rate is
        // the number that says whether LiveGroupCacheSeconds is set sensibly.
        using var capture = new MetricCapture();
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, metrics: capture.Metrics);

        // Three calls, not two: with two, a swap of hit and miss still yields one of each and passes.
        await resolver.TryResolvePermissionsAsync(UserOid);
        await resolver.TryResolvePermissionsAsync(UserOid);
        await resolver.TryResolvePermissionsAsync(UserOid);

        capture.Total("vitally.cache.lookups", ("cache", "group_membership"), ("result", "miss")).Should().Be(1);
        capture.Total("vitally.cache.lookups", ("cache", "group_membership"), ("result", "hit")).Should().Be(2);
    }

    [Fact]
    public async Task Resolves_ViaTransitiveMembers_SoNestedGroupsAreHonoured()
    {
        // Regression guard: nested (transitive) membership only works if we query /transitiveMembers,
        // not the direct-only /members relationship.
        var handler = new RecordingHandler(new HashSet<string>());
        var resolver = Build(handler);

        await resolver.TryResolvePermissionsAsync(UserOid);

        handler.RequestedUris.Should().NotBeEmpty();
        handler.RequestedUris.Should().OnlyContain(u => u.Contains("/transitiveMembers", StringComparison.Ordinal));
        handler.RequestedUris.Should().OnlyContain(u => !u.Contains("/members?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grants_ReadAndWrite_ForEditorGroupMembership()
    {
        var handler = new RecordingHandler(new HashSet<string> { EditorGroup });
        var resolver = Build(handler);

        var permissions = await resolver.TryResolvePermissionsAsync(UserOid);

        permissions!.Permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write"]);
    }

    [Fact]
    public async Task Grants_AllTiers_ForAdminGroupMembership()
    {
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler);

        var permissions = await resolver.TryResolvePermissionsAsync(UserOid);

        permissions!.Permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write", "vitally:delete"]);
    }

    [Fact]
    public async Task Grants_Nothing_WhenNotAMemberOfAnyGroup()
    {
        var handler = new RecordingHandler(new HashSet<string>());
        var resolver = Build(handler);

        var permissions = await resolver.TryResolvePermissionsAsync(UserOid);

        permissions.Should().NotBeNull();
        permissions!.Permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsNull_WhenGraphFails_WithNoRetainedCopy_SoTheCallerDenies()
    {
        var handler = new RecordingHandler(new HashSet<string>(), HttpStatusCode.Forbidden);
        var resolver = Build(handler);

        var permissions = await resolver.TryResolvePermissionsAsync(UserOid);

        permissions.Should().BeNull("a Graph failure is fail-degraded, not fail-open");
    }

    [Fact]
    public async Task CachesResult_SoSecondCallDoesNotReHitGraph()
    {
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var resolver = Build(handler, cache);

        await resolver.TryResolvePermissionsAsync(UserOid);
        var callsAfterFirst = handler.RequestedUris.Count;
        await resolver.TryResolvePermissionsAsync(UserOid);

        handler.RequestedUris.Count.Should().Be(callsAfterFirst, "the per-user result is cached for the TTL");
    }

    // ---- Serve-stale-on-error (#106) ----------------------------------------------------------
    // Graph is the sole source of entitlement, so a Graph outage would otherwise deny every user.
    // These cover the fallback that prevents that: the last known-good result for that user, served
    // for a bounded window. #108 removed the claim fall-through that once sat below it, so beyond
    // the window the answer is a denial — see StaleEntitlementCompositionTests, which drives that
    // through the composed host.

    [Fact]
    public async Task ServesStaleResult_WhenGraphFails_AfterAnEarlierSuccess()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { EditorGroup });
        var resolver = Build(handler, timeProvider: clock);

        var fresh = await resolver.TryResolvePermissionsAsync(UserOid);
        fresh!.Permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write"]);

        // Past the 60s fresh TTL but well inside the stale window, with Graph now down.
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;

        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served!.Permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write"],
            "a Graph outage must not revoke a user whose tier was known good two minutes earlier");
    }

    [Fact]
    public async Task CountsAStaleServe_AsAMiss_NotAHit()
    {
        // The stale serve answers from the retained copy, but only AFTER asking Graph and failing — the
        // cache did not spare the round-trip, so it is a miss. Counting it as a hit would make the hit
        // rate look healthiest exactly when Graph is down.
        using var capture = new MetricCapture();
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { EditorGroup });
        var resolver = Build(handler, timeProvider: clock, metrics: capture.Metrics);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served!.ServedStale.Should().BeTrue("a precondition: this must be the stale path");
        capture.Total("vitally.cache.lookups", ("cache", "group_membership"), ("result", "miss")).Should().Be(2);
        capture.Total("vitally.cache.lookups", ("cache", "group_membership"), ("result", "hit")).Should().Be(0);
    }

    [Fact]
    public async Task LogsWarning_WithStaleness_WhenServingStale()
    {
        var clock = new FakeClock(ClockStart);
        var logger = new CapturingLogger<GraphGroupPermissionResolver>();
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, timeProvider: clock, logger: logger);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(300));
        handler.Status = HttpStatusCode.InternalServerError;
        await resolver.TryResolvePermissionsAsync(UserOid);

        // Exactly one warning: serving stale replaces the plain "lookup failed" message rather than
        // adding to it, so an outage reads as one line per call instead of two.
        var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("300", "the operator needs to know how stale the served result is");
        warning.Message.Should().Contain(UserOid, "the subject id is the audit key");
        warning.Message.Should().NotContain("@", "never log the caller's email — subject id only");
    }

    [Fact]
    public async Task StopsServingStale_OnceTheStaleWindowElapses()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler, timeProvider: clock, staleSeconds: 600);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(601));
        handler.Status = HttpStatusCode.BadGateway;

        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served.Should().BeNull("bounded staleness is the trade — past the window the copy is not served");
    }

    [Fact]
    public async Task DoesNotServeStale_WhenTheStaleWindowIsZero()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler, timeProvider: clock, staleSeconds: 0);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;

        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served.Should().BeNull("zero disables stale serving, restoring the previous behaviour exactly");
    }

    [Fact]
    public async Task PrefersFreshResult_OverStale_WhenGraphIsHealthy()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);

        // Demoted to reader while Graph is perfectly healthy.
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.MemberGroupIds = new HashSet<string> { ReaderGroup };

        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served!.Permissions.Should().BeEquivalentTo(["vitally:read"],
            "a stale copy must never beat a successful lookup, or a revocation would not take effect");
    }

    // ---- Reporting staleness to the caller (#161) ---------------------------------------------
    // The audit record carries whether the tier it names was served from the retained copy. That is
    // only honest if the resolver says so itself: it is the one component that knows which branch it
    // took, and anything downstream inferring it would be guessing.

    [Fact]
    public async Task ReportsNotStale_ForAFreshLookup()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, timeProvider: clock);

        var resolved = await resolver.TryResolvePermissionsAsync(UserOid);

        resolved.Should().NotBeNull();
        resolved!.ServedStale.Should().BeFalse("Graph answered this very call");
        resolved.Age.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task ReportsNotStale_WithItsAge_ForACacheHitInsideTheFreshWindow()
    {
        // A cache hit inside LiveGroupCacheSeconds is the live check working as designed, not a
        // degradation — Graph confirmed it within the window the deployment accepts as current. So
        // it is not "stale"; but its age is still reported, because it is not zero either.
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(30));

        var resolved = await resolver.TryResolvePermissionsAsync(UserOid);

        resolved!.ServedStale.Should().BeFalse();
        resolved.Age.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ReportsServedStale_WithItsAge_WhenServingTheRetainedCopy()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { EditorGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;

        var resolved = await resolver.TryResolvePermissionsAsync(UserOid);

        resolved!.Permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write"]);
        resolved.ServedStale.Should().BeTrue("this tier was not confirmed by Graph on this call");
        resolved.Age.Should().Be(TimeSpan.FromSeconds(120),
            "stale by twenty seconds and stale by fifty-nine minutes are different claims");
    }

    [Fact]
    public async Task ReportsTheStaleAge_AsOfFailureTime_NotAsOfWhenTheLookupBegan()
    {
        // Same reasoning as the window decision below: a Graph timeout can burn the whole client
        // timeout, and an age measured from before the attempt would under-report how stale the
        // served answer is by exactly that much.
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(100));
        handler.Status = HttpStatusCode.GatewayTimeout;
        handler.OnRequest = () => clock.Advance(TimeSpan.FromSeconds(10));

        var resolved = await resolver.TryResolvePermissionsAsync(UserOid);

        resolved!.ServedStale.Should().BeTrue();
        resolved.Age.Should().Be(TimeSpan.FromSeconds(110));
    }

    [Fact]
    public async Task ReportsServedStale_OnEveryCall_ThroughAnOutage_NotOnlyTheFirst()
    {
        // Pins that a stale serve is never written back to the cache. Re-caching it — the obvious
        // way to damp repeated Graph attempts during an outage — with a fresh ResolvedAt would bring
        // the next call back through the fresh-window branch labelled Confirmed: out-of-date data
        // recorded as checked, and a fresh window stretched past a revocation.
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        (await resolver.TryResolvePermissionsAsync(UserOid))!.ServedStale.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1));
        var next = await resolver.TryResolvePermissionsAsync(UserOid);

        next!.ServedStale.Should().BeTrue("Graph is still down, so this answer is still the retained copy");
        next.Age.Should().Be(TimeSpan.FromSeconds(121), "and it is a second older, not reset");
    }

    [Fact]
    public async Task ReportsNotStale_OnceGraphRecovers_AfterServingStale()
    {
        // The flag describes the answer, not the resolver's history: a stale serve must not leave
        // the next fresh answer marked stale.
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;
        (await resolver.TryResolvePermissionsAsync(UserOid))!.ServedStale.Should().BeTrue();

        handler.Status = HttpStatusCode.OK;
        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = await resolver.TryResolvePermissionsAsync(UserOid);

        recovered!.ServedStale.Should().BeFalse();
        recovered.Age.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task DoesNotServeOneUsersStaleResult_ToAnother()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;

        var other = await resolver.TryResolvePermissionsAsync(OtherUserOid);

        other.Should().BeNull("the retained copy is per user — one caller's tier must never be served to another");
    }

    [Fact]
    public async Task ReHitsGraph_OnceTheFreshTtlLapses_DespiteRetainingAStaleCopy()
    {
        // Guards the obvious way to implement this wrongly: extending the cache entry's lifetime to
        // the stale window without splitting the freshness decision out would silently stretch the
        // live check's cache from 60s to an hour, and revocations would stop propagating.
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { ReaderGroup });
        var resolver = Build(handler, timeProvider: clock);

        await resolver.TryResolvePermissionsAsync(UserOid);
        var callsAfterFirst = handler.RequestedUris.Count;

        clock.Advance(TimeSpan.FromSeconds(61));
        await resolver.TryResolvePermissionsAsync(UserOid);

        handler.RequestedUris.Count.Should().BeGreaterThan(callsAfterFirst,
            "retaining a stale copy must not extend the fresh cache window");
    }

    [Fact]
    public async Task DoesNotServeStale_WhenTheFailingLookupItselfCarriesTheEntryOutOfTheWindow()
    {
        // The stale decision must be made as of the moment the lookup *failed*, not the moment it
        // started. A Graph timeout can burn the whole client timeout, so a call that begins inside the
        // window can finish outside it, and reading the clock once up front would serve a copy that
        // is by then out of bounds — and under-report its age in the warning.
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler, timeProvider: clock, staleSeconds: 600);

        await resolver.TryResolvePermissionsAsync(UserOid);

        clock.Advance(TimeSpan.FromSeconds(595));      // still inside the 600s window...
        handler.Status = HttpStatusCode.GatewayTimeout;
        handler.OnRequest = () => clock.Advance(TimeSpan.FromSeconds(10)); // ...but not once it fails

        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served.Should().BeNull("the window is enforced as of failure time, not as of when the call began");
    }

    [Fact]
    public async Task ReturnsNull_ForBlankObjectId()
    {
        var handler = new RecordingHandler(new HashSet<string>());
        var resolver = Build(handler);

        (await resolver.TryResolvePermissionsAsync("  ")).Should().BeNull();
        handler.RequestedUris.Should().BeEmpty();
    }
}
