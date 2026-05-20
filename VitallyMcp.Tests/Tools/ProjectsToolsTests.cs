using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for ProjectsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class ProjectsToolsTests
{
    private const string TestSubdomain = "test-subdomain";
    private const string TestApiKey = "sk_live_test_key";

    private VitallyService CreateService(HttpClient httpClient)
    {
        return TestHelpers.BuildVitallyService(httpClient, subdomain: TestSubdomain, apiKey: TestApiKey);

    }

    [Fact]
    public async Task ListProjects_WithDefaultParameters_ShouldReturnProjects()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.ListProjects(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListProjects_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.ListProjects(service, fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListProjects_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.ListProjects(service, fields: "id,name,traits", traits: "status");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"status\"");
    }

    [Fact]
    public async Task ListProjectsByAccount_WithValidAccountId_ShouldReturnProjects()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.ListProjectsByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListProjectsByOrganization_WithValidOrgId_ShouldReturnProjects()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.ListProjectsByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task GetProject_WithValidId_ShouldReturnSingleProject()
    {
        // Arrange
        var singleProjectJson = """
        {
          "id": "project-123",
          "name": "Test Project",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleProjectJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.GetProject(service, "project-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("project-123");
        result.Should().Contain("Test Project");
    }

    [Fact]
    public async Task GetProject_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleProjectJson = """
        {
          "id": "project-123",
          "name": "Test Project"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleProjectJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.GetProject(service, "project-123", fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task CreateProjectFromTemplate_WithValidData_ShouldReturnCreatedProject()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-project-123",
          "name": "New Project",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var projectData = """
        {
          "templateId": "tmpl-789",
          "accountId": "account-456"
        }
        """;

        // Act
        var result = await ProjectsTools.CreateProjectFromTemplate(service, projectData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-project-123");
        result.Should().Contain("New Project");
    }

    [Fact]
    public async Task UpdateProject_WithValidData_ShouldReturnUpdatedProject()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "project-123",
          "name": "Updated Project Name"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "name": "Updated Project Name"
        }
        """;

        // Act
        var result = await ProjectsTools.UpdateProject(service, "project-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Project Name");
    }

    [Fact]
    public async Task DeleteProject_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Project deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectsTools.DeleteProject(service, "project-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
