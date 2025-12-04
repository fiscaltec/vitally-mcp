using FluentAssertions;
using System.Text.Json;

namespace VitallyMcp.Tests;

/// <summary>
/// Tests for VitallyService JSON filtering, field selection, trait filtering, and pagination.
/// </summary>
public class VitallyServiceTests
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

    #region Default Field Tests

    [Fact]
    public async Task GetResourcesAsync_AccountsWithNoFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should include default fields
        firstAccount.TryGetProperty("id", out _).Should().BeTrue();
        firstAccount.TryGetProperty("name", out _).Should().BeTrue();
        firstAccount.TryGetProperty("createdAt", out _).Should().BeTrue();
        firstAccount.TryGetProperty("healthScore", out _).Should().BeTrue();
        firstAccount.TryGetProperty("mrr", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstAccount.TryGetProperty("traits", out _).Should().BeFalse();

        // Assert - Should NOT include large text fields
        firstAccount.TryGetProperty("largeTextField", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_OrganizationsWithNoFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleOrganizationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("organizations", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstOrg = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should include default fields
        firstOrg.TryGetProperty("id", out _).Should().BeTrue();
        firstOrg.TryGetProperty("name", out _).Should().BeTrue();
        firstOrg.TryGetProperty("healthScore", out _).Should().BeTrue();
        firstOrg.TryGetProperty("mrr", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstOrg.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_UsersWithNoFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("users", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstUser = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should include default fields
        firstUser.TryGetProperty("id", out _).Should().BeTrue();
        firstUser.TryGetProperty("name", out _).Should().BeTrue();
        firstUser.TryGetProperty("email", out _).Should().BeTrue();
        firstUser.TryGetProperty("accountId", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstUser.TryGetProperty("traits", out _).Should().BeFalse();
    }

    #endregion

    #region Field Filtering Tests

    [Fact]
    public async Task GetResourcesAsync_WithSpecificFields_ShouldReturnOnlyRequestedFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request only id and name
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should only have id and name
        firstAccount.TryGetProperty("id", out _).Should().BeTrue();
        firstAccount.TryGetProperty("name", out _).Should().BeTrue();
        firstAccount.TryGetProperty("createdAt", out _).Should().BeFalse();
        firstAccount.TryGetProperty("healthScore", out _).Should().BeFalse();
        firstAccount.TryGetProperty("mrr", out _).Should().BeFalse();
        firstAccount.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithNonExistentFields_ShouldSkipThem()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request existing and non-existent fields
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,nonExistentField1,nonExistentField2");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should only have existing fields
        firstAccount.TryGetProperty("id", out _).Should().BeTrue();
        firstAccount.TryGetProperty("name", out _).Should().BeTrue();
        firstAccount.TryGetProperty("nonExistentField1", out _).Should().BeFalse();
        firstAccount.TryGetProperty("nonExistentField2", out _).Should().BeFalse();
    }

    #endregion

    #region Trait Filtering Tests

    [Fact]
    public async Task GetResourcesAsync_WithTraitsButNoTraitFilter_ShouldReturnAllTraits()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request traits field without specifying which traits
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,traits");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];
        var traits = firstAccount.GetProperty("traits");

        // Assert - Should include all traits from the response
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("paymentMethod", out _).Should().BeTrue();
        traits.TryGetProperty("plan", out _).Should().BeTrue();
        traits.TryGetProperty("employees", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetResourcesAsync_WithSpecificTraits_ShouldReturnOnlyRequestedTraits()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request only specific traits
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,traits", traits: "industry,paymentMethod");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];
        var traits = firstAccount.GetProperty("traits");

        // Assert - Should only have requested traits
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("paymentMethod", out _).Should().BeTrue();
        traits.TryGetProperty("plan", out _).Should().BeFalse();
        traits.TryGetProperty("employees", out _).Should().BeFalse();
        traits.TryGetProperty("customField1", out _).Should().BeFalse();
        traits.TryGetProperty("customField2", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithTraitsFieldButNoTraitsInResponse_ShouldNotIncludeTraits()
    {
        // Arrange - Response without traits object
        var jsonWithoutTraits = """
        {
          "results": [
            {
              "id": "account-123",
              "name": "Test Account"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(jsonWithoutTraits);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,traits");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should not have traits property since it wasn't in original response
        firstAccount.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithNonExistentTraits_ShouldSkipThem()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request traits that don't exist
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,traits", traits: "industry,nonExistentTrait1,nonExistentTrait2");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];
        var traits = firstAccount.GetProperty("traits");

        // Assert - Should only have existing traits
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("nonExistentTrait1", out _).Should().BeFalse();
        traits.TryGetProperty("nonExistentTrait2", out _).Should().BeFalse();
    }

    #endregion

    #region Pagination Tests

    [Fact]
    public async Task GetResourcesAsync_WithPagination_ShouldPreserveNextCursor()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Next cursor should be preserved
        jsonDoc.RootElement.TryGetProperty("next", out var nextProperty).Should().BeTrue();
        nextProperty.GetString().Should().Be("cursor-next-page");
    }

    [Fact]
    public async Task GetResourcesAsync_WithoutNextCursor_ShouldNotIncludeNext()
    {
        // Arrange - Response without next cursor
        var jsonWithoutNext = """
        {
          "results": [
            {
              "id": "account-123",
              "name": "Test Account"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(jsonWithoutNext);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Next should not be present
        jsonDoc.RootElement.TryGetProperty("next", out _).Should().BeFalse();
    }

    #endregion

    #region Empty Results Tests

    [Fact]
    public async Task GetResourcesAsync_WithEmptyResults_ShouldReturnEmptyArray()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetEmptyResultsJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var results = jsonDoc.RootElement.GetProperty("results");

        // Assert
        results.GetArrayLength().Should().Be(0);
    }

    #endregion

    #region GetResourceByIdAsync Tests

    [Fact]
    public async Task GetResourceByIdAsync_WithDefaultFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourceByIdAsync("accounts", "account-123");
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Should include default fields
        jsonDoc.RootElement.TryGetProperty("id", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("name", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("healthScore", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        jsonDoc.RootElement.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourceByIdAsync_WithSpecificFields_ShouldReturnOnlyRequestedFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourceByIdAsync("accounts", "account-123", fields: "id,name");
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Should only have requested fields
        jsonDoc.RootElement.TryGetProperty("id", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("name", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("healthScore", out _).Should().BeFalse();
        jsonDoc.RootElement.TryGetProperty("mrr", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourceByIdAsync_WithTraits_ShouldFilterTraitsCorrectly()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourceByIdAsync("accounts", "account-123", fields: "id,traits", traits: "industry");
        var jsonDoc = JsonDocument.Parse(result);
        var traits = jsonDoc.RootElement.GetProperty("traits");

        // Assert - Should only have industry trait
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("paymentMethod", out _).Should().BeFalse();
    }

    #endregion

    #region Multiple Resources Tests

    [Fact]
    public async Task GetResourcesAsync_WithMultipleResults_ShouldFilterAllResults()
    {
        // Arrange
        var multipleAccountsJson = """
        {
          "results": [
            {
              "id": "account-1",
              "name": "Account 1",
              "healthScore": 80,
              "mrr": 1000,
              "traits": { "industry": "Tech", "plan": "Pro" }
            },
            {
              "id": "account-2",
              "name": "Account 2",
              "healthScore": 90,
              "mrr": 2000,
              "traits": { "industry": "Finance", "plan": "Enterprise" }
            },
            {
              "id": "account-3",
              "name": "Account 3",
              "healthScore": 70,
              "mrr": 500,
              "traits": { "industry": "Healthcare", "plan": "Basic" }
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(multipleAccountsJson);
        var service = CreateService(mockClient);

        // Act - Request only id and name
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name");
        var jsonDoc = JsonDocument.Parse(result);
        var results = jsonDoc.RootElement.GetProperty("results");

        // Assert - All 3 results should be filtered
        results.GetArrayLength().Should().Be(3);

        foreach (var account in results.EnumerateArray())
        {
            account.TryGetProperty("id", out _).Should().BeTrue();
            account.TryGetProperty("name", out _).Should().BeTrue();
            account.TryGetProperty("healthScore", out _).Should().BeFalse();
            account.TryGetProperty("mrr", out _).Should().BeFalse();
            account.TryGetProperty("traits", out _).Should().BeFalse();
        }
    }

    #endregion

    #region Resource-Specific Default Field Tests

    [Fact]
    public async Task GetResourcesAsync_Tasks_ShouldHaveCorrectDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("tasks", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstTask = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should have task-specific default fields
        firstTask.TryGetProperty("id", out _).Should().BeTrue();
        firstTask.TryGetProperty("name", out _).Should().BeTrue();
        firstTask.TryGetProperty("dueDate", out _).Should().BeTrue();
        firstTask.TryGetProperty("completedAt", out _).Should().BeTrue();
        firstTask.TryGetProperty("assignedToId", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstTask.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_Notes_ShouldHaveCorrectDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("notes", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstNote = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should have note-specific default fields
        firstNote.TryGetProperty("id", out _).Should().BeTrue();
        firstNote.TryGetProperty("subject", out _).Should().BeTrue();
        firstNote.TryGetProperty("noteDate", out _).Should().BeTrue();
        firstNote.TryGetProperty("authorId", out _).Should().BeTrue();
        firstNote.TryGetProperty("categoryId", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstNote.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_Projects_ShouldHaveCorrectDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("projects", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstProject = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should have project-specific default fields
        firstProject.TryGetProperty("id", out _).Should().BeTrue();
        firstProject.TryGetProperty("name", out _).Should().BeTrue();
        firstProject.TryGetProperty("accountId", out _).Should().BeTrue();
        firstProject.TryGetProperty("organizationId", out _).Should().BeTrue();
        firstProject.TryGetProperty("archivedAt", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstProject.TryGetProperty("traits", out _).Should().BeFalse();
    }

    #endregion
}
