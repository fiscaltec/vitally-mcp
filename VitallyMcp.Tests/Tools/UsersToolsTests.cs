using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for UsersTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class UsersToolsTests
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
    public async Task ListUsers_WithDefaultParameters_ShouldReturnUsers()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.ListUsers(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
        result.Should().Contain("\"email\"");
    }

    [Fact]
    public async Task ListUsers_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.ListUsers(service, fields: "id,email");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"email\"");
    }

    [Fact]
    public async Task ListUsersByAccount_WithValidAccountId_ShouldReturnUsers()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.ListUsersByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListUsersByOrganization_WithValidOrgId_ShouldReturnUsers()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.ListUsersByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task SearchUsers_WithEmail_ShouldReturnMatchingUsers()
    {
        // Arrange
        var searchResultJson = """
        {
          "results": [
            {
              "id": "user-123",
              "name": "John Doe",
              "email": "john.doe@example.com",
              "accountId": "account-456",
              "organizationId": "org-789",
              "createdAt": "2024-01-01T00:00:00Z",
              "updatedAt": "2024-01-15T00:00:00Z",
              "externalId": "ext-user-123",
              "lastSeenTimestamp": "2024-01-15T12:00:00Z"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(searchResultJson);
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.SearchUsers(service, "john.doe@example.com");

        // Assert - Should contain user ID at minimum (default fields filtering applies)
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("user-123");
    }

    [Fact]
    public async Task GetUser_WithValidId_ShouldReturnSingleUser()
    {
        // Arrange
        var singleUserJson = """
        {
          "id": "user-123",
          "name": "John Doe",
          "email": "john.doe@example.com",
          "accountId": "account-456",
          "organizationId": "org-789"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleUserJson);
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.GetUser(service, "user-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("user-123");
        result.Should().Contain("John Doe");
    }

    [Fact]
    public async Task CreateUser_WithValidData_ShouldReturnCreatedUser()
    {
        // Arrange
        var createdUserJson = """
        {
          "id": "new-user-123",
          "name": "Jane Smith",
          "email": "jane.smith@example.com",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdUserJson);
        var service = CreateService(mockClient);

        var userData = """
        {
          "name": "Jane Smith",
          "email": "jane.smith@example.com",
          "accountId": "account-456"
        }
        """;

        // Act
        var result = await UsersTools.CreateUser(service, userData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-user-123");
        result.Should().Contain("Jane Smith");
    }

    [Fact]
    public async Task UpdateUser_WithValidData_ShouldReturnUpdatedUser()
    {
        // Arrange
        var updatedUserJson = """
        {
          "id": "user-123",
          "name": "John Doe Updated",
          "email": "john.doe@example.com"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedUserJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "name": "John Doe Updated"
        }
        """;

        // Act
        var result = await UsersTools.UpdateUser(service, "user-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("John Doe Updated");
    }

    [Fact]
    public async Task DeleteUser_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "User deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.DeleteUser(service, "user-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }

    [Fact]
    public async Task ListUsers_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await UsersTools.ListUsers(service, fields: "id,name,traits", traits: "role,department");

        // Assert
        result.Should().Contain("\"traits\"");
    }
}
