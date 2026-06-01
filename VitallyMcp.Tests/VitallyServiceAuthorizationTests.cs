using System.Net;
using FluentAssertions;

namespace VitallyMcp.Tests;

/// <summary>
/// Verifies that RBAC enforcement is wired into the single choke point
/// (<c>VitallyService.SendAsync</c>) so it applies to every tool, and that the HTTP verb maps to
/// the correct permission tier.
/// </summary>
public class VitallyServiceAuthorizationTests
{
    [Fact]
    public async Task Read_Denied_WhenCallerLacksReadPermission()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = TestHelpers.BuildVitallyService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("vitally:write"));

        var act = () => service.GetResourcesAsync("accounts");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*vitally:read*");
    }

    [Fact]
    public async Task Write_Denied_WhenCallerOnlyHasRead()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = TestHelpers.BuildVitallyService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("vitally:read"));

        var act = () => service.CreateResourceAsync("accounts", "{}");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*vitally:write*");
    }

    [Fact]
    public async Task Delete_Denied_WhenCallerOnlyHasWrite()
    {
        var http = TestHelpers.CreateMockHttpClient("{}");
        var service = TestHelpers.BuildVitallyService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("vitally:read", "vitally:write"));

        var act = () => service.DeleteResourceAsync("accounts", "account-123");
        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*vitally:delete*");
    }

    [Fact]
    public async Task Read_Allowed_WhenCallerHasReadPermission()
    {
        var http = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = TestHelpers.BuildVitallyService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("vitally:read"));

        var result = await service.GetResourcesAsync("accounts");
        result.Should().Contain("account-123");
    }

    [Fact]
    public async Task Delete_Allowed_WhenCallerHasDeletePermission()
    {
        var (http, handler) = TestHelpers.CreateMockHttpClientWithHandler("{}", HttpStatusCode.OK);
        var service = TestHelpers.BuildVitallyService(
            http, authorizer: TestHelpers.BuildEnabledAuthorizer("vitally:delete"));

        var act = () => service.DeleteResourceAsync("accounts", "account-123");
        await act.Should().NotThrowAsync();
    }
}
