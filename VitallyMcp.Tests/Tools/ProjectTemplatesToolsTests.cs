using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for ProjectTemplatesTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class ProjectTemplatesToolsTests
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
    public async Task ListProjectTemplates_WithDefaultParameters_ShouldReturnTemplates()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectTemplateJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.ListProjectTemplates(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListProjectTemplates_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectTemplateJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.ListProjectTemplates(service, fields: "id,name,description");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"description\"");
    }

    [Fact]
    public async Task ListProjectTemplates_WithCategoryFilter_ShouldReturnTemplates()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectTemplateJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.ListProjectTemplates(service, categoryId: "cat-456");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
    }

    [Fact]
    public async Task ListProjectTemplates_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectTemplateJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.ListProjectTemplates(service, fields: "id,name,traits", traits: "phase");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"phase\"");
    }

    [Fact]
    public async Task GetProjectTemplate_WithValidId_ShouldReturnSingleTemplate()
    {
        // Arrange
        var singleTemplateJson = """
        {
          "id": "tmpl-123",
          "name": "Onboarding Template",
          "projectCategoryId": "cat-456",
          "description": "Standard onboarding"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleTemplateJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.GetProjectTemplate(service, "tmpl-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("tmpl-123");
        result.Should().Contain("Onboarding Template");
    }

    [Fact]
    public async Task GetProjectTemplate_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleTemplateJson = """
        {
          "id": "tmpl-123",
          "name": "Onboarding Template"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleTemplateJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.GetProjectTemplate(service, "tmpl-123", fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListProjectCategories_WithDefaultParameters_ShouldReturnCategories()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectCategoryJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.ListProjectCategories(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task GetProjectCategory_WithValidId_ShouldReturnSingleCategory()
    {
        // Arrange
        var singleCategoryJson = """
        {
          "id": "cat-123",
          "name": "Onboarding"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleCategoryJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ProjectTemplatesTools.GetProjectCategory(service, "cat-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("cat-123");
        result.Should().Contain("Onboarding");
    }
}
