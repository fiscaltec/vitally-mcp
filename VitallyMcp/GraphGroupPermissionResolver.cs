using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Resolves live Vitally permissions from Microsoft Graph group membership. Uses the server's
/// managed identity (<see cref="TokenCredential"/>) to call Graph's <c>checkMemberGroups</c>,
/// which returns — transitively — the subset of the configured group ids the user belongs to.
/// Results are cached per user for <see cref="ToolAuthorizationOptions.LiveGroupCacheSeconds"/>.
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
            var memberOf = await CheckMemberGroupsAsync(userObjectId, groupIds, cancellationToken);
            var permissions = MapGroupsToPermissions(memberOf);
            _cache.Set(cacheKey, permissions, TimeSpan.FromSeconds(_options.LiveGroupCacheSeconds));
            return permissions;
        }
        catch (Exception ex)
        {
            // Fail-degraded: log and signal the authorizer to fall back to the token claim, so a
            // Graph outage doesn't lock everyone out. Don't cache the failure.
            _logger.LogWarning(ex, "Live group permission lookup failed for {UserObjectId}; falling back to token claim.", userObjectId);
            return null;
        }
    }

    private async Task<HashSet<string>> CheckMemberGroupsAsync(string userObjectId, string[] groupIds, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), cancellationToken);

        var payload = JsonSerializer.Serialize(new { groupIds });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{GraphBase}/users/{Uri.EscapeDataString(userObjectId)}/checkMemberGroups")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Graph checkMemberGroups returned {(int)response.StatusCode}: {body}");
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var id = item.GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    result.Add(id);
                }
            }
        }
        return result;
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
