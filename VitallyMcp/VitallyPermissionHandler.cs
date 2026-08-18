using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

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
public class VitallyPermissionHandler(ToolAuthorizer authorizer, AuditLogger audit)
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
            return;
        }

        // Left unsucceeded — for tools/list the SDK drops the tool from the advertised list, and for
        // tools/call it refuses the invocation.
        //
        // Only the refused invocation is an audit event. The SDK evaluates this requirement once per
        // tool per tools/list (93 evaluations, uncached), so auditing every non-success would emit 37
        // spurious "denied" records for a reader on every single tools/list — normal filtering, not a
        // denial, and enough volume to bury the real ones. The resource type is what distinguishes
        // the two: a refused call carries RequestContext<CallToolRequestParams>, discovery filtering
        // carries RequestContext<ListToolsRequestParams>.
        if (context.Resource is RequestContext<CallToolRequestParams> callToolContext)
        {
            audit.LogToolCallDenied(context.User, callToolContext.Params?.Name, requirement.Permission);
        }
    }
}
