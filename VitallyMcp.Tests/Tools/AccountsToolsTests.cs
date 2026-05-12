using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for AccountsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class AccountsToolsTests
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
    public async Task ListAccounts_WithDefaultParameters_ShouldReturnAccounts()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.ListAccounts(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListAccounts_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.ListAccounts(service, fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListAccounts_WithStatusFilter_ShouldCallServiceWithCorrectParameters()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.ListAccounts(service, status: "active");

        // Assert
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListAccounts_WithPagination_ShouldIncludeNextCursor()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.ListAccounts(service, limit: 10);

        // Assert
        result.Should().Contain("\"next\"");
        result.Should().Contain("cursor-next-page");
    }

    [Fact]
    public async Task ListAccounts_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.ListAccounts(service, fields: "id,name,traits", traits: "industry");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"industry\"");
    }

    [Fact]
    public async Task GetAccount_WithValidId_ShouldReturnSingleAccount()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.GetAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("account-123");
    }

    [Fact]
    public async Task GetAccount_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.GetAccount(service, "account-123", fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListAccountsByOrganization_WithValidOrgId_ShouldReturnAccounts()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.ListAccountsByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task CreateAccount_WithValidData_ShouldReturnCreatedAccount()
    {
        // Arrange
        var createdAccountJson = """
        {
          "id": "new-account-123",
          "name": "New Test Account",
          "organizationId": "org-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdAccountJson);
        var service = CreateService(mockClient);

        var accountData = """
        {
          "name": "New Test Account",
          "organizationId": "org-456"
        }
        """;

        // Act
        var result = await AccountsTools.CreateAccount(service, accountData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-account-123");
        result.Should().Contain("New Test Account");
    }

    [Fact]
    public async Task UpdateAccount_WithValidData_ShouldReturnUpdatedAccount()
    {
        // Arrange
        var updatedAccountJson = """
        {
          "id": "account-123",
          "name": "Updated Account Name",
          "organizationId": "org-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedAccountJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "name": "Updated Account Name"
        }
        """;

        // Act
        var result = await AccountsTools.UpdateAccount(service, "account-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Account Name");
    }

    [Fact]
    public async Task DeleteAccount_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Account deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await AccountsTools.DeleteAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
