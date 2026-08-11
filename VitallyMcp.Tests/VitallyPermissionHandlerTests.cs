using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp.Tests;

public class VitallyPermissionHandlerTests
{
    private static AuthorizationHandlerContext ContextFor(ClaimsPrincipal user, string permission)
    {
        var requirement = new VitallyPermissionRequirement(permission);
        return new AuthorizationHandlerContext([requirement], user, resource: null);
    }

    /// <summary>
    /// Builds the handler alongside the sink its <see cref="AuditLogger"/> writes to, so tests can
    /// assert on whether a non-success produced an audit record.
    /// </summary>
    private static (VitallyPermissionHandler handler, CapturingLogger<AuditLogger> auditSink) Build(bool enabled)
    {
        var auditSink = new CapturingLogger<AuditLogger>();
        var audit = new AuditLogger(
            Microsoft.Extensions.Options.Options.Create(new AuditOptions { Enabled = true }),
            auditSink);
        return (new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled), audit), auditSink);
    }

    [Fact]
    public async Task Succeeds_WhenCallerHoldsThePermission()
    {
        var (handler, _) = Build(enabled: true);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "vitally:read")], "test"));
        var context = ContextFor(user, "vitally:read");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task DoesNotSucceed_WhenCallerLacksThePermission()
    {
        var (handler, auditSink) = Build(enabled: true);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "vitally:read")], "test"));
        var context = ContextFor(user, "vitally:delete");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();

        // resource is null here (neither a call nor a list), so there is nothing to audit. Only a
        // RequestContext<CallToolRequestParams> resource represents a refused invocation.
        auditSink.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Succeeds_WhenAuthorizationDisabled()
    {
        // With RBAC off (or NoAuth dev mode) discovery must not be filtered — otherwise local
        // development sees an empty tool list.
        var (handler, _) = Build(enabled: false);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = ContextFor(user, "vitally:delete");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task DoesNotSucceed_WhenCallerIsUnauthenticated()
    {
        // An unauthenticated identity carries no permissions; leaving it unsucceeded is what keeps
        // an anonymous caller from discovering any tool at all.
        var (handler, _) = Build(enabled: true);
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = ContextFor(user, "vitally:read");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }
}
