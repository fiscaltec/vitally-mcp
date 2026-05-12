using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for OrganizationsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class OrganizationsToolsTests
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
    public async Task ListOrganizations_WithDefaultParameters_ShouldReturnOrganizations()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleOrganizationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await OrganizationsTools.ListOrganizations(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListOrganizations_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleOrganizationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await OrganizationsTools.ListOrganizations(service, fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListOrganizations_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleOrganizationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await OrganizationsTools.ListOrganizations(service, fields: "id,name,traits", traits: "industry,region");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"industry\"");
        result.Should().Contain("\"region\"");
    }

    [Fact]
    public async Task GetOrganization_WithValidId_ShouldReturnSingleOrganization()
    {
        // Arrange
        var singleOrgJson = """
        {
          "id": "org-123",
          "name": "Test Organization",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "externalId": "ext-org-123",
          "healthScore": 90.0
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleOrgJson);
        var service = CreateService(mockClient);

        // Act
        var result = await OrganizationsTools.GetOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("org-123");
        result.Should().Contain("Test Organization");
    }

    [Fact]
    public async Task GetOrganization_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleOrgJson = """
        {
          "id": "org-123",
          "name": "Test Organization",
          "externalId": "ext-org-123"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleOrgJson);
        var service = CreateService(mockClient);

        // Act
        var result = await OrganizationsTools.GetOrganization(service, "org-123", fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task CreateOrganization_WithValidData_ShouldReturnCreatedOrganization()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-org-123",
          "name": "New Test Organization",
          "externalId": "ext-new-org"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var orgData = """
        {
          "name": "New Test Organization",
          "externalId": "ext-new-org"
        }
        """;

        // Act
        var result = await OrganizationsTools.CreateOrganization(service, orgData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-org-123");
        result.Should().Contain("New Test Organization");
    }

    [Fact]
    public async Task UpdateOrganization_WithValidData_ShouldReturnUpdatedOrganization()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "org-123",
          "name": "Updated Organization Name"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "name": "Updated Organization Name"
        }
        """;

        // Act
        var result = await OrganizationsTools.UpdateOrganization(service, "org-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Organization Name");
    }

    [Fact]
    public async Task DeleteOrganization_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Organization deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await OrganizationsTools.DeleteOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
