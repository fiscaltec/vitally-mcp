using System.Net;
using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
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
public class GraphGroupPermissionResolverTests
{
    private const string ReaderGroup = "71451cc9-f5df-44ee-8ed1-3acc41a911eb";
    private const string EditorGroup = "19b9d659-284c-4f93-b1c3-a6354db1027c";
    private const string AdminGroup = "70b48a20-d4b1-47dc-a132-21bc99272a86";
    private const string UserOid = "675ebdda-7590-4d79-8ec3-a2d17ab029ba";

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
    /// requested URI so tests can assert the relationship and the call count.
    /// </summary>
    private sealed class RecordingHandler(ISet<string> memberGroupIds, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public List<string> RequestedUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            RequestedUris.Add(uri);

            if (status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"error\":\"boom\"}") });
            }

            var isMember = memberGroupIds.Any(id => uri.Contains(id, StringComparison.OrdinalIgnoreCase));
            var body = isMember ? "{\"value\":[{\"id\":\"" + UserOid + "\"}]}" : "{\"value\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static GraphGroupPermissionResolver Build(RecordingHandler handler, IMemoryCache? cache = null)
    {
        var options = new ToolAuthorizationOptions
        {
            Enabled = true,
            LiveGroupCheck = true,
            ReaderGroupId = ReaderGroup,
            EditorGroupId = EditorGroup,
            AdminGroupId = AdminGroup,
        };
        return new GraphGroupPermissionResolver(
            new HttpClient(handler),
            new StubTokenCredential(),
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            Options.Create(options),
            NullLogger<GraphGroupPermissionResolver>.Instance);
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

    [Fact]
    public async Task ReturnsNull_ForBlankObjectId()
    {
        var handler = new RecordingHandler(new HashSet<string>());
        var resolver = Build(handler);

        (await resolver.TryResolvePermissionsAsync("  ")).Should().BeNull();
        handler.RequestedUris.Should().BeEmpty();
    }
}
