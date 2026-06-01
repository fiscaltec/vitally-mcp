using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class ToolAuthorizerTests
{
    private static ToolAuthorizer Build(
        bool enabled = true,
        bool noAuth = false,
        ClaimsPrincipal? user = null,
        ToolAuthorizationOptions? options = null)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = user is null ? null : new DefaultHttpContext { User = user }
        };
        return new ToolAuthorizer(
            Options.Create(options ?? new ToolAuthorizationOptions { Enabled = enabled }),
            Options.Create(new OAuthOptions { NoAuth = noAuth }),
            accessor);
    }

    private static ClaimsPrincipal UserWithPermissions(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permissions", p)), "Test"));

    private static ClaimsPrincipal UserWithScope(string scope) =>
        new(new ClaimsIdentity(new[] { new Claim("scope", scope) }, "Test"));

    [Theory]
    [InlineData("GET", "vitally:read")]
    [InlineData("POST", "vitally:write")]
    [InlineData("PUT", "vitally:write")]
    [InlineData("PATCH", "vitally:write")]
    [InlineData("DELETE", "vitally:delete")]
    public void RequiredPermission_MapsVerbToTier(string method, string expected)
    {
        var authorizer = Build();
        authorizer.RequiredPermission(new HttpMethod(method)).Should().Be(expected);
    }

    [Fact]
    public void HasPermission_True_FromPermissionsClaim()
    {
        ToolAuthorizer.HasPermission(UserWithPermissions("vitally:read", "vitally:write"), "vitally:write")
            .Should().BeTrue();
    }

    [Fact]
    public void HasPermission_True_FromSpaceDelimitedScope()
    {
        ToolAuthorizer.HasPermission(UserWithScope("openid vitally:read vitally:delete"), "vitally:delete")
            .Should().BeTrue();
    }

    [Fact]
    public void HasPermission_False_WhenAbsent()
    {
        ToolAuthorizer.HasPermission(UserWithPermissions("vitally:read"), "vitally:delete")
            .Should().BeFalse();
    }

    [Fact]
    public void HasPermission_True_FromCustomNamespacedClaim()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("https://vitally.fiscaltec.com/permissions", "vitally:write") }, "Test"));
        ToolAuthorizer.HasPermission(user, "vitally:write", "https://vitally.fiscaltec.com/permissions")
            .Should().BeTrue();
    }

    [Fact]
    public void HasPermission_IgnoresCustomClaim_WhenNotConfigured()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("https://vitally.fiscaltec.com/permissions", "vitally:write") }, "Test"));
        ToolAuthorizer.HasPermission(user, "vitally:write", customClaimType: null)
            .Should().BeFalse();
    }

    [Fact]
    public void EnsureAuthorized_Allows_WhenPermissionPresent()
    {
        var authorizer = Build(user: UserWithPermissions("vitally:write"));
        var act = () => authorizer.EnsureAuthorized(HttpMethod.Post);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureAuthorized_Throws_WhenPermissionMissing()
    {
        var authorizer = Build(user: UserWithPermissions("vitally:read"));
        var act = () => authorizer.EnsureAuthorized(HttpMethod.Delete);
        act.Should().Throw<UnauthorizedAccessException>().WithMessage("*vitally:delete*");
    }

    [Fact]
    public void EnsureAuthorized_Throws_WhenNoAuthenticatedUser()
    {
        var authorizer = Build(user: null);
        var act = () => authorizer.EnsureAuthorized(HttpMethod.Get);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void EnsureAuthorized_NoOp_WhenDisabled()
    {
        var authorizer = Build(enabled: false, user: null);
        var act = () => authorizer.EnsureAuthorized(HttpMethod.Delete);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureAuthorized_NoOp_WhenNoAuthDevMode()
    {
        var authorizer = Build(noAuth: true, user: null);
        var act = () => authorizer.EnsureAuthorized(HttpMethod.Delete);
        act.Should().NotThrow();
    }
}
