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

    public void Validate()
    {
        ReadPermission = ReadPermission?.Trim() ?? string.Empty;
        WritePermission = WritePermission?.Trim() ?? string.Empty;
        DeletePermission = DeletePermission?.Trim() ?? string.Empty;
        CustomPermissionsClaim = CustomPermissionsClaim?.Trim() ?? string.Empty;

        if (!Enabled)
        {
            return;
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
