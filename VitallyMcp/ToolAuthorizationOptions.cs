namespace VitallyMcp;

/// <summary>
/// Server-side authorisation policy for Vitally tool calls. Maps the HTTP verb of each Vitally
/// API call to a required permission, which is checked against the caller's JWT. This is the
/// hard backstop behind the advisory <c>ReadOnly</c>/<c>Destructive</c> tool flags — those flags
/// only guide MCP clients; this enforces access regardless of what the client does.
///
/// Permission strings must match the permissions defined on the Auth0 API (Resource Server) with
/// "Enable RBAC" + "Add Permissions in the Access Token" turned on. To collapse to two tiers,
/// point <see cref="DeletePermission"/> at the same value as <see cref="WritePermission"/>.
/// </summary>
public class ToolAuthorizationOptions
{
    public const string SectionName = "Authorization";

    /// <summary>
    /// When false, no permission check is performed (any authenticated caller may invoke any
    /// tool). Defaults to true so production is locked down by default — leaving it unset is the
    /// secure choice, not the open one.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Permission required for read operations (HTTP GET).</summary>
    public string ReadPermission { get; set; } = "vitally:read";

    /// <summary>Permission required for create/update operations (HTTP POST/PUT/PATCH).</summary>
    public string WritePermission { get; set; } = "vitally:write";

    /// <summary>Permission required for delete operations (HTTP DELETE).</summary>
    public string DeletePermission { get; set; } = "vitally:delete";

    /// <summary>
    /// Optional namespaced claim that may carry the permission values, in addition to the standard
    /// Auth0 RBAC <c>permissions</c> claim and the space-delimited <c>scope</c> claim. Use this when
    /// an Auth0 post-login Action maps Entra group membership to permissions via a custom claim
    /// rather than Auth0 role assignment. Auth0 requires custom claims to be namespaced (a URL on a
    /// domain you control). Leave empty to check only <c>permissions</c> and <c>scope</c>.
    /// </summary>
    public string CustomPermissionsClaim { get; set; } = "https://vitally.fiscaltec.com/permissions";

    /// <summary>
    /// When true, permissions are resolved from the caller's <b>live</b> Entra group membership
    /// (via Microsoft Graph, cached for <see cref="LiveGroupCacheSeconds"/>) rather than trusting
    /// the frozen token claim — so group changes (grants and especially revocations) take effect
    /// within the cache window regardless of token age. The token claim remains the automatic
    /// fallback if the Graph lookup fails (fail-degraded, not fail-open: an empty claim still denies).
    /// Requires the server's managed identity to hold Microsoft Graph <c>GroupMember.Read.All</c>.
    /// </summary>
    public bool LiveGroupCheck { get; set; }

    /// <summary>TTL (seconds) for the per-user live group-membership cache. Default 60.</summary>
    public int LiveGroupCacheSeconds { get; set; } = 60;

    /// <summary>Entra security-group object id whose members get the read tier (<c>vitally:read</c>).</summary>
    public string ReaderGroupId { get; set; } = string.Empty;

    /// <summary>Entra security-group object id whose members get read + write.</summary>
    public string EditorGroupId { get; set; } = string.Empty;

    /// <summary>Entra security-group object id whose members get read + write + delete.</summary>
    public string AdminGroupId { get; set; } = string.Empty;

    /// <summary>All configured group ids (non-empty), used to scope the Graph membership check.</summary>
    public IEnumerable<string> ConfiguredGroupIds =>
        new[] { ReaderGroupId, EditorGroupId, AdminGroupId }.Where(g => !string.IsNullOrWhiteSpace(g));

    public void Validate()
    {
        ReadPermission = ReadPermission?.Trim() ?? string.Empty;
        WritePermission = WritePermission?.Trim() ?? string.Empty;
        DeletePermission = DeletePermission?.Trim() ?? string.Empty;
        CustomPermissionsClaim = CustomPermissionsClaim?.Trim() ?? string.Empty;
        ReaderGroupId = ReaderGroupId?.Trim() ?? string.Empty;
        EditorGroupId = EditorGroupId?.Trim() ?? string.Empty;
        AdminGroupId = AdminGroupId?.Trim() ?? string.Empty;

        if (!Enabled)
        {
            return;
        }

        if (LiveGroupCheck)
        {
            var groupIds = ConfiguredGroupIds.ToArray();
            if (groupIds.Length == 0)
            {
                throw new InvalidOperationException(
                    "Authorization:LiveGroupCheck is true but no group ids are configured. Set at least one of "
                    + "Authorization:ReaderGroupId / EditorGroupId / AdminGroupId to an Entra group object id.");
            }
            var badGuid = groupIds.FirstOrDefault(g => !Guid.TryParse(g, out _));
            if (badGuid is not null)
            {
                throw new InvalidOperationException(
                    $"Authorization group ids must be GUIDs (Entra group object ids); got '{badGuid}'.");
            }
            if (LiveGroupCacheSeconds < 0)
            {
                throw new InvalidOperationException("Authorization:LiveGroupCacheSeconds cannot be negative.");
            }
        }

        if (string.IsNullOrWhiteSpace(ReadPermission)
            || string.IsNullOrWhiteSpace(WritePermission)
            || string.IsNullOrWhiteSpace(DeletePermission))
        {
            throw new InvalidOperationException(
                "Authorization:ReadPermission, Authorization:WritePermission and Authorization:DeletePermission must all be set when Authorization:Enabled is true. "
                + "Set Authorization:Enabled=false only for local development.");
        }
    }
}
