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

    [Fact]
    public async Task Succeeds_WhenCallerHoldsThePermission()
    {
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "vitally:read")], "test"));
        var context = ContextFor(user, "vitally:read");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task DoesNotSucceed_WhenCallerLacksThePermission()
    {
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "vitally:read")], "test"));
        var context = ContextFor(user, "vitally:delete");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Succeeds_WhenAuthorizationDisabled()
    {
        // With RBAC off (or NoAuth dev mode) discovery must not be filtered — otherwise local
        // development sees an empty tool list.
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: false));
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
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = ContextFor(user, "vitally:read");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }
}
