using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class ToolAuthorizerTests
{
    private const string AdminGroup = "70b48a20-d4b1-47dc-a132-21bc99272a86";
    private const string ReaderGroup = "71451cc9-f5df-44ee-8ed1-3acc41a911eb";
    private const string SubWithOid = "waad|fiscal-entra|675ebdda-7590-4d79-8ec3-a2d17ab029ba";

    private sealed class StubResolver(IReadOnlySet<string>? result) : IGroupPermissionResolver
    {
        public string? LastObjectId { get; private set; }
        public Task<IReadOnlySet<string>?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default)
        {
            LastObjectId = userObjectId;
            return Task.FromResult(result);
        }
    }

    private static ToolAuthorizer Build(
        bool enabled = true,
        bool noAuth = false,
        ClaimsPrincipal? user = null,
        ToolAuthorizationOptions? options = null,
        IGroupPermissionResolver? resolver = null)
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = user is null ? null : new DefaultHttpContext { User = user }
        };
        return new ToolAuthorizer(
            Options.Create(options ?? new ToolAuthorizationOptions { Enabled = enabled }),
            Options.Create(new OAuthOptions { NoAuth = noAuth }),
            accessor,
            resolver);
    }

    private static ClaimsPrincipal UserWithPermissions(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permissions", p)), "Test"));

    private static ClaimsPrincipal UserWithScope(string scope) =>
        new(new ClaimsIdentity(new[] { new Claim("scope", scope) }, "Test"));

    private static ClaimsPrincipal UserWithSub(string sub, params string[] permissions)
    {
        var claims = new List<Claim> { new("sub", sub) };
        claims.AddRange(permissions.Select(p => new Claim("permissions", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ToolAuthorizationOptions LiveOptions() => new()
    {
        Enabled = true,
        LiveGroupCheck = true,
        ReaderGroupId = ReaderGroup,
        AdminGroupId = AdminGroup
    };

    [Theory]
    [InlineData("GET", "vitally:read")]
    [InlineData("POST", "vitally:write")]
    [InlineData("PUT", "vitally:write")]
    [InlineData("PATCH", "vitally:write")]
    [InlineData("DELETE", "vitally:delete")]
    public void RequiredPermission_MapsVerbToTier(string method, string expected)
    {
        Build().RequiredPermission(new HttpMethod(method)).Should().Be(expected);
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
        ToolAuthorizer.HasPermission(UserWithPermissions("vitally:read"), "vitally:delete").Should().BeFalse();
    }

    [Fact]
    public void HasPermission_True_FromCustomNamespacedClaim()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("https://vitally.fiscaltec.com/permissions", "vitally:write") }, "Test"));
        ToolAuthorizer.HasPermission(user, "vitally:write", "https://vitally.fiscaltec.com/permissions").Should().BeTrue();
    }

    [Fact]
    public void HasPermission_IgnoresCustomClaim_WhenNotConfigured()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("https://vitally.fiscaltec.com/permissions", "vitally:write") }, "Test"));
        ToolAuthorizer.HasPermission(user, "vitally:write", customClaimType: null).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAuthorized_Allows_WhenPermissionPresent()
    {
        var authorizer = Build(user: UserWithPermissions("vitally:write"));
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Post)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAuthorized_Throws_WhenPermissionMissing()
    {
        var authorizer = Build(user: UserWithPermissions("vitally:read"));
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*vitally:delete*");
    }

    [Fact]
    public async Task EnsureAuthorized_Throws_WhenNoAuthenticatedUser()
    {
        var authorizer = Build(user: null);
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Get))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task EnsureAuthorized_NoOp_WhenDisabled()
    {
        var authorizer = Build(enabled: false, user: null);
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureAuthorized_NoOp_WhenNoAuthDevMode()
    {
        var authorizer = Build(noAuth: true, user: null);
        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
    }

    // ---- Live group check ----

    [Fact]
    public async Task LiveCheck_Allows_WhenLiveGroupsGrantPermission()
    {
        var resolver = new StubResolver(new HashSet<string> { "vitally:read", "vitally:write", "vitally:delete" });
        var authorizer = Build(user: UserWithSub(SubWithOid), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
        resolver.LastObjectId.Should().Be("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "the oid is parsed from the sub");
    }

    [Fact]
    public async Task LiveCheck_Engages_WhenSubIsMappedToNameIdentifier()
    {
        // Regression: JwtBearer maps "sub" -> ClaimTypes.NameIdentifier in production, so the live
        // path must still find the object id from the mapped claim (no raw "sub" present here).
        var resolver = new StubResolver(new HashSet<string> { "vitally:read", "vitally:write", "vitally:delete" });
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, SubWithOid) }, "Test"));
        var authorizer = Build(user: user, options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
        resolver.LastObjectId.Should().Be("675ebdda-7590-4d79-8ec3-a2d17ab029ba");
    }

    [Fact]
    public async Task LiveCheck_Denies_WhenLiveGroupsLackPermission()
    {
        // Live membership says read-only — must override a stale token claim that still has delete.
        var resolver = new StubResolver(new HashSet<string> { "vitally:read" });
        var authorizer = Build(user: UserWithSub(SubWithOid, "vitally:delete"), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*vitally:delete*");
    }

    [Fact]
    public async Task LiveCheck_FallsBackToClaim_WhenResolverUnavailable()
    {
        // Resolver returns null (e.g. Graph down) -> fall back to the token claim, which grants delete.
        var resolver = new StubResolver(null);
        var authorizer = Build(user: UserWithSub(SubWithOid, "vitally:delete"), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task LiveCheck_FallbackDenies_WhenResolverUnavailableAndClaimLacksPermission()
    {
        var resolver = new StubResolver(null);
        var authorizer = Build(user: UserWithSub(SubWithOid, "vitally:read"), options: LiveOptions(), resolver: resolver);

        await authorizer.Invoking(a => a.EnsureAuthorizedAsync(HttpMethod.Delete))
            .Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ReadOnly_DeniesMutatingVerbs_EvenWhenAuthDisabled(string method)
    {
        // ReadOnly is independent of Enabled — denies writes even with RBAC off.
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(new HttpMethod(method));

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*read-only*");
    }

    [Fact]
    public async Task ReadOnly_AllowsGet()
    {
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Get);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReadOnly_DeniesWrite_EvenInNoAuthDev()
    {
        var authorizer = Build(noAuth: true, options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Delete);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*read-only*");
    }

    [Fact]
    public async Task ReadOnlyFalse_DoesNotBlockWrites()
    {
        // Default ReadOnly=false: with Enabled=false the write passes (unchanged behaviour).
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = false });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Post);

        await act.Should().NotThrowAsync();
    }
}
