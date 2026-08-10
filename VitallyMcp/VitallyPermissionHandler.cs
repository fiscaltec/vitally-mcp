using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp;

/// <summary>
/// Evaluates <see cref="VitallyPermissionRequirement"/> by delegating to <see cref="ToolAuthorizer"/>,
/// so tools/list discovery filtering and the <see cref="VitallyService"/> enforcement backstop share
/// one resolution path and cannot drift apart.
///
/// <para>
/// This filters <b>discovery</b>. It is not the security boundary — that remains
/// <c>VitallyService.SendAsync</c>, which authorises every upstream call regardless of what the
/// client managed to see or invoke.
/// </para>
/// </summary>
public class VitallyPermissionHandler(ToolAuthorizer authorizer)
    : AuthorizationHandler<VitallyPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VitallyPermissionRequirement requirement)
    {
        if (await authorizer.IsAuthorizationBypassedAsync())
        {
            // RBAC disabled or NoAuth dev mode: don't filter discovery, or local dev sees no tools.
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true
            && await authorizer.HasEffectivePermissionAsync(context.User, requirement.Permission))
        {
            context.Succeed(requirement);
        }
        // Otherwise leave unsucceeded — the SDK filter drops the tool from tools/list.
    }
}
