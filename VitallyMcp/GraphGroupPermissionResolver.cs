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
/// the user is a member — by listing the group's members filtered to that user id. Checking from
/// the group side needs only <c>GroupMember.Read.All</c>; the alternative
/// <c>POST /users/{id}/checkMemberGroups</c> additionally requires a user-read permission
/// (User.ReadBasic.All), which we deliberately avoid. Results are cached per user for
/// <see cref="ToolAuthorizationOptions.LiveGroupCacheSeconds"/>.
///
/// Requires the managed identity to hold the Graph application permission <c>GroupMember.Read.All</c>.
/// On any failure the method returns <c>null</c> so the authorizer falls back to the token claim.
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

    public GraphGroupPermissionResolver(
        HttpClient httpClient,
        TokenCredential credential,
        IMemoryCache cache,
        IOptions<ToolAuthorizationOptions> options,
        ILogger<GraphGroupPermissionResolver> logger)
    {
        _httpClient = httpClient;
        _credential = credential;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlySet<string>?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userObjectId))
        {
            return null;
        }

        var cacheKey = $"live-perms::{userObjectId}";
        if (_cache.TryGetValue<IReadOnlySet<string>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var groupIds = _options.ConfiguredGroupIds.ToArray();
        if (groupIds.Length == 0)
        {
            return null; // Nothing to check against — let the caller fall back to the claim.
        }

        try
        {
            var memberOf = await ResolveMemberGroupsAsync(userObjectId, groupIds, cancellationToken);
            var permissions = MapGroupsToPermissions(memberOf);
            _cache.Set(cacheKey, permissions, TimeSpan.FromSeconds(_options.LiveGroupCacheSeconds));
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
            // Fail-degraded: log and signal the authorizer to fall back to the token claim, so a
            // Graph outage doesn't lock everyone out. Don't cache the failure.
            _logger.LogWarning(ex, "Live group permission lookup failed for {UserObjectId}; falling back to token claim.", userObjectId);
            return null;
        }
    }

    // Determine which of the configured groups the user belongs to, checking from the group side
    // (list a group's members filtered to this user id). Listing group members needs only
    // GroupMember.Read.All — unlike POST /users/{id}/checkMemberGroups, which also requires reading
    // the user object (User.ReadBasic.All). Direct membership is sufficient here because the
    // sg-vitally-* groups are assigned to users directly.
    private async Task<HashSet<string>> ResolveMemberGroupsAsync(string userObjectId, string[] groupIds, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);
        var member = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var groupId in groupIds)
        {
            // $filter on id is an advanced query, so $count=true + ConsistencyLevel: eventual are
            // required. Returns the user only if they are a member of this group.
            var filter = Uri.EscapeDataString($"id eq '{userObjectId}'");
            var url = $"{GraphBase}/groups/{Uri.EscapeDataString(groupId)}/members?$count=true&$select=id&$filter={filter}";
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

    // Cumulative tiers, mirroring the Auth0 post-login Action: admin ⊇ editor ⊇ reader.
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
