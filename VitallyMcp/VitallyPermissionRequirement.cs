using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp;

/// <summary>
/// Authorisation requirement carrying one <c>vitally:*</c> permission. Exists so the MCP SDK's
/// <c>AddAuthorizationFilters()</c> can evaluate tool-level <c>[Authorize]</c> attributes through the
/// standard ASP.NET Core policy pipeline while still resolving permissions through
/// <c>ToolAuthorizer.HasEffectivePermissionAsync</c>, so discovery filtering and the
/// <c>VitallyService.SendAsync</c> backstop cannot disagree. With <c>Authorization:LiveGroupCheck</c>
/// on — every deployed target — that order is fresh Graph → stale Graph → <b>deny</b>; there is no
/// fall-through to a token claim, which #108 removed.
/// </summary>
/// <param name="permission">The required permission, e.g. <c>vitally:write</c>.</param>
public class VitallyPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
