using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Resolves live Vitally permissions from Microsoft Graph group membership. Uses the server's
/// managed identity (<see cref="TokenCredential"/>) to check, for each configured group, whether
/// the user is a member — by listing the group's <b>transitive</b> members filtered to that user
/// id, so membership via a nested (department) group counts, not just direct membership. Checking
/// from the group side needs only <c>GroupMember.Read.All</c>; the alternative
/// <c>POST /users/{id}/checkMemberGroups</c> additionally requires a user-read permission
/// (User.ReadBasic.All), which we deliberately avoid.
///
/// Requires the managed identity to hold the Graph application permission <c>GroupMember.Read.All</c>.
///
/// <para><b>One cache entry, two windows.</b> Each successful lookup is stored with the time it was
/// resolved, and its age is compared against two thresholds:
/// <see cref="ToolAuthorizationOptions.LiveGroupCacheSeconds"/> decides whether it can be served
/// without asking Graph at all, and <see cref="ToolAuthorizationOptions.LiveGroupStaleSeconds"/>
/// decides whether it may still be served as a <i>fallback</i> after a Graph call has failed. Only
/// when neither applies does the method return <c>null</c> — which, with
/// <see cref="ToolAuthorizationOptions.LiveGroupCheck"/> on, means the authorizer <b>denies</b>.
/// #108 removed the fall-through to the token claim, so the order is fresh Graph → stale set →
/// deny.</para>
///
/// <para>The two thresholds are kept deliberately separate. Retaining a copy for an hour must not
/// stretch the live check's own cache from a minute to an hour — that would stop revocations
/// propagating, which is the entire reason the live check exists. Age is measured with an injected
/// <see cref="TimeProvider"/> rather than by cache expiry, both because the decision needs the age
/// itself for the warning log and because <see cref="IMemoryCache"/> expiry cannot be wound forward
/// in a test.</para>
/// </summary>
public class GraphGroupPermissionResolver : IGroupPermissionResolver
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly IMemoryCache _cache;
    private readonly ToolAuthorizationOptions _options;
    private readonly ILogger<GraphGroupPermissionResolver> _logger;
    private readonly TimeProvider _timeProvider;

    public GraphGroupPermissionResolver(
        HttpClient httpClient,
        TokenCredential credential,
        IMemoryCache cache,
        IOptions<ToolAuthorizationOptions> options,
        ILogger<GraphGroupPermissionResolver> logger,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _credential = credential;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>A successful lookup plus when it was resolved, so its age drives both windows.</summary>
    private sealed record ResolvedPermissions(IReadOnlySet<string> Permissions, DateTimeOffset ResolvedAt);

    public async Task<IReadOnlySet<string>?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userObjectId))
        {
            return null;
        }

        // The entry is keyed per user. That is load-bearing rather than tidy: it is what stops one
        // caller's retained tier ever being served to another during an outage.
        var cacheKey = $"live-perms::{userObjectId}";
        _cache.TryGetValue<ResolvedPermissions>(cacheKey, out var lastKnownGood);
        var now = _timeProvider.GetUtcNow();

        if (lastKnownGood is not null && IsWithin(lastKnownGood, now, _options.LiveGroupCacheSeconds))
        {
            return lastKnownGood.Permissions;
        }

        var groupIds = _options.ConfiguredGroupIds.ToArray();
        if (groupIds.Length == 0)
        {
            // Nothing to check against. Returns null like any other unresolvable case, which with
            // LiveGroupCheck on means the authorizer denies. Unreachable on a configuration-bound
            // server, by two separate routes: with Authorization:Enabled=true, Validate() refuses
            // LiveGroupCheck with no group ids at boot; with Enabled=false it returns before
            // reaching that check, but ToolAuthorizer then bypasses and never calls a resolver at
            // all. So arriving here means an options instance built in code — a test, or a future
            // call site resolving permissions outside ToolAuthorizer.
            return null;
        }

        try
        {
            var memberOf = await ResolveMemberGroupsAsync(userObjectId, groupIds, cancellationToken);
            var permissions = MapGroupsToPermissions(memberOf);
            // Retained for whichever window is longer, since the entry now serves both roles.
            var retentionSeconds = Math.Max(1, Math.Max(_options.LiveGroupCacheSeconds, _options.LiveGroupStaleSeconds));
            // ResolvedAt is deliberately the pre-call `now`, NOT the time the call returned. It is a
            // lower bound on when this answer was true, so every age computed from it is >= the real
            // age. Re-reading the clock here would make the entry look *fresher* than it is and
            // silently extend the fresh window by the call duration — delaying revocation
            // propagation, which is the whole reason the live check exists. Erring old is the safe
            // direction; erring young is not.
            _cache.Set(cacheKey, new ResolvedPermissions(permissions, now), TimeSpan.FromSeconds(retentionSeconds));
            return permissions;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine caller cancellation (client disconnect / shutdown) — propagate rather than
            // masking it as a fallback. A Graph *timeout* cancels its own token, not this one, so
            // it still falls through to the fail-degraded path below.
            throw;
        }
        catch (Exception ex)
        {
            // Fail-degraded, then closed. Prefer this caller's last known-good tier, so a Graph
            // outage does not revoke someone whose membership was confirmed minutes ago; when there
            // is no usable copy this returns null and the authorizer denies (#108 removed the
            // token-claim tier that used to sit below). Never cache the failure itself.
            // Read the clock again: `now` predates the attempt, and a Graph timeout can burn the
            // whole client timeout before arriving here. Both the decision and the reported age must
            // be as of failure time, or a lookup that began inside the window could be served after
            // it had left it — and the warning would under-report how stale the answer is.
            var failedAt = _timeProvider.GetUtcNow();

            if (lastKnownGood is not null && _options.LiveGroupStaleSeconds > 0
                && IsWithin(lastKnownGood, failedAt, _options.LiveGroupStaleSeconds))
            {
                // One warning, not two: an outage should read as a single line per call. Subject id
                // only — never the caller's email, per the audit rules.
                _logger.LogWarning(
                    ex,
                    "Live group permission lookup failed for {UserObjectId}; serving the last known-good permission set, stale by {StaleSeconds}s (limit {StaleLimitSeconds}s).",
                    userObjectId,
                    (long)(failedAt - lastKnownGood.ResolvedAt).TotalSeconds,
                    _options.LiveGroupStaleSeconds);
                return lastKnownGood.Permissions;
            }

            // Deliberately does NOT say "falling back to the token claim". It used to, and that became
            // false when #108 removed the claim tier: with LiveGroupCheck on — every deployed target —
            // ToolAuthorizer denies on this null. A log line promising a fallback during the one incident
            // where it matters would send whoever is reading it looking for a tier that cannot engage.
            // The caller decides and logs the outcome; this line reports only what happened here.
            _logger.LogWarning(
                ex,
                "Live group permission lookup failed for {UserObjectId} and no usable retained set exists "
                + "(stale window {StaleLimitSeconds}s). Returning no result; with Authorization:LiveGroupCheck "
                + "enabled the caller will be denied.",
                userObjectId,
                _options.LiveGroupStaleSeconds);
            return null;
        }
    }

    // A window of 0 means "off" rather than "expires instantly", so it is never treated as a hit.
    private static bool IsWithin(ResolvedPermissions entry, DateTimeOffset now, int windowSeconds) =>
        windowSeconds > 0 && (now - entry.ResolvedAt).TotalSeconds <= windowSeconds;

    // Determine which of the configured groups the user belongs to, checking from the group side
    // (list a group's transitive members filtered to this user id). transitiveMembers expands
    // nested groups, so a user who is in a department group that is itself a member of an
    // sg-vitally-* group is resolved correctly — not only users assigned to the sg-vitally-* group
    // directly. Listing group members needs only GroupMember.Read.All — unlike
    // POST /users/{id}/checkMemberGroups, which also requires reading the user object
    // (User.ReadBasic.All).
    private async Task<HashSet<string>> ResolveMemberGroupsAsync(string userObjectId, string[] groupIds, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);
        var member = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var groupId in groupIds)
        {
            // $filter on id is an advanced query, so $count=true + ConsistencyLevel: eventual are
            // required. Returns the user only if they are a transitive member of this group.
            var filter = Uri.EscapeDataString($"id eq '{userObjectId}'");
            var url = $"{GraphBase}/groups/{Uri.EscapeDataString(groupId)}/transitiveMembers?$count=true&$select=id&$filter={filter}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.Add("ConsistencyLevel", "eventual");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Graph group members query returned {(int)response.StatusCode}: {body}");
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("value", out var value)
                && value.ValueKind == JsonValueKind.Array
                && value.GetArrayLength() > 0)
            {
                member.Add(groupId);
            }
        }

        return member;
    }

    // Cumulative tiers: admin ⊇ editor ⊇ reader.
    private HashSet<string> MapGroupsToPermissions(HashSet<string> memberGroupIds)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        void GrantIfMember(string groupId, params string[] perms)
        {
            if (!string.IsNullOrWhiteSpace(groupId) && memberGroupIds.Contains(groupId))
            {
                foreach (var p in perms)
                {
                    permissions.Add(p);
                }
            }
        }

        GrantIfMember(_options.ReaderGroupId, _options.ReadPermission);
        GrantIfMember(_options.EditorGroupId, _options.ReadPermission, _options.WritePermission);
        GrantIfMember(_options.AdminGroupId, _options.ReadPermission, _options.WritePermission, _options.DeletePermission);

        return permissions;
    }
}
