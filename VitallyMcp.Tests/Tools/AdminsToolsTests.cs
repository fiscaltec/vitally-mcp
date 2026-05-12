using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for AdminsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class AdminsToolsTests
{
    private const string TestSubdomain = "test-subdomain";
    private const string TestApiKey = "sk_live_test_key";

    private VitallyService CreateService(HttpClient httpClient)
    {
        var config = new VitallyConfig
        {
            Subdomain = TestSubdomain,
            ApiKey = TestApiKey
        };
        return new VitallyService(httpClient, config);
    }

    [Fact]
    public async Task SearchAdmins_WithEmail_ShouldReturnMatchingAdmins()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAdminJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AdminsTools.SearchAdmins(service, "admin@example.com");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("admin-123");
        result.Should().Contain("admin@example.com");
    }

    [Fact]
    public async Task SearchAdmins_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAdminJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AdminsTools.SearchAdmins(service, "admin@example.com", fields: "id,email");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"email\"");
    }
}
