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
        int staleSeconds = 3600)
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
            timeProvider);
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

        permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write"]);
    }

    [Fact]
    public async Task Grants_AllTiers_ForAdminGroupMembership()
    {
        var handler = new RecordingHandler(new HashSet<string> { AdminGroup });
        var resolver = Build(handler);

        var permissions = await resolver.TryResolvePermissionsAsync(UserOid);

        permissions.Should().BeEquivalentTo(["vitally:read", "vitally:write", "vitally:delete"]);
    }

    [Fact]
    public async Task Grants_Nothing_WhenNotAMemberOfAnyGroup()
    {
        var handler = new RecordingHandler(new HashSet<string>());
        var resolver = Build(handler);

        var permissions = await resolver.TryResolvePermissionsAsync(UserOid);

        permissions.Should().NotBeNull();
        permissions.Should().BeEmpty();
    }

    [Fact]
    public async Task ReturnsNull_WhenGraphFails_SoCallerFallsBackToClaim()
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
    // Once Auth0 is retired, Graph is the sole source of entitlement, so a Graph outage would deny
    // every user. These cover the replacement fallback: the last known-good result for that user,
    // served for a bounded window. Note the claim fall-through *below* this still exists while Auth0
    // is live — removing it is #108's job, so nothing here asserts a denial in its place.

    [Fact]
    public async Task ServesStaleResult_WhenGraphFails_AfterAnEarlierSuccess()
    {
        var clock = new FakeClock(ClockStart);
        var handler = new RecordingHandler(new HashSet<string> { EditorGroup });
        var resolver = Build(handler, timeProvider: clock);

        var fresh = await resolver.TryResolvePermissionsAsync(UserOid);
        fresh.Should().BeEquivalentTo(["vitally:read", "vitally:write"]);

        // Past the 60s fresh TTL but well inside the stale window, with Graph now down.
        clock.Advance(TimeSpan.FromSeconds(120));
        handler.Status = HttpStatusCode.ServiceUnavailable;

        var served = await resolver.TryResolvePermissionsAsync(UserOid);

        served.Should().BeEquivalentTo(["vitally:read", "vitally:write"],
            "a Graph outage must not revoke a user whose tier was known good two minutes earlier");
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

        served.Should().BeEquivalentTo(["vitally:read"],
            "a stale copy must never beat a successful lookup, or a revocation would not take effect");
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
    public async Task ReturnsNull_ForBlankObjectId()
    {
        var handler = new RecordingHandler(new HashSet<string>());
        var resolver = Build(handler);

        (await resolver.TryResolvePermissionsAsync("  ")).Should().BeNull();
        handler.RequestedUris.Should().BeEmpty();
    }
}
