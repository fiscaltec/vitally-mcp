using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp;

/// <summary>
/// Authorisation requirement carrying one <c>vitally:*</c> permission. Exists so the MCP SDK's
/// <c>AddAuthorizationFilters()</c> can evaluate tool-level <c>[Authorize]</c> attributes through the
/// standard ASP.NET Core policy pipeline while still resolving permissions the way this server
/// always has (live Entra groups, falling back to the token claim).
/// </summary>
/// <param name="permission">The required permission, e.g. <c>vitally:write</c>.</param>
public class VitallyPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
