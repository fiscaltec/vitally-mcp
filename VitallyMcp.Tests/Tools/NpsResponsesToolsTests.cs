using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for NpsResponsesTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class NpsResponsesToolsTests
{
    private const string TestSubdomain = "test-subdomain";
    private const string TestApiKey = "sk_live_test_key";

    private VitallyService CreateService(HttpClient httpClient)
    {
        return TestHelpers.BuildVitallyService(httpClient, subdomain: TestSubdomain, apiKey: TestApiKey);

    }

    [Fact]
    public async Task ListNpsResponses_WithDefaultParameters_ShouldReturnResponses()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNpsResponseJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.ListNpsResponses(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"score\"");
    }

    [Fact]
    public async Task ListNpsResponses_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNpsResponseJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.ListNpsResponses(service, fields: "id,score,feedback");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"score\"");
        result.Should().Contain("\"feedback\"");
    }

    [Fact]
    public async Task ListNpsResponses_WithTargetFilter_ShouldReturnResponses()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNpsResponseJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.ListNpsResponses(service, target: "organization");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
    }

    [Fact]
    public async Task ListNpsResponsesByAccount_WithValidAccountId_ShouldReturnResponses()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNpsResponseJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.ListNpsResponsesByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListNpsResponsesByOrganization_WithValidOrgId_ShouldReturnResponses()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNpsResponseJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.ListNpsResponsesByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task GetNpsResponse_WithValidId_ShouldReturnSingleResponse()
    {
        // Arrange
        var singleNpsJson = """
        {
          "id": "nps-123",
          "score": 9,
          "feedback": "Great product!",
          "userId": "user-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleNpsJson);
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.GetNpsResponse(service, "nps-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("nps-123");
        result.Should().Contain("Great product!");
    }

    [Fact]
    public async Task GetNpsResponse_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleNpsJson = """
        {
          "id": "nps-123",
          "score": 9
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleNpsJson);
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.GetNpsResponse(service, "nps-123", fields: "id,score");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"score\"");
    }

    [Fact]
    public async Task CreateNpsResponse_WithValidData_ShouldReturnCreatedResponse()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-nps-123",
          "score": 10,
          "userId": "user-456",
          "respondedAt": "2024-01-15T12:00:00Z"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var npsData = """
        {
          "userId": "user-456",
          "respondedAt": "2024-01-15T12:00:00Z",
          "score": 10
        }
        """;

        // Act
        var result = await NpsResponsesTools.CreateNpsResponse(service, npsData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-nps-123");
    }

    [Fact]
    public async Task UpdateNpsResponse_WithValidData_ShouldReturnUpdatedResponse()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "nps-123",
          "score": 8,
          "feedback": "Updated feedback"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "userId": "user-456",
          "respondedAt": "2024-01-15T12:00:00Z",
          "score": 8,
          "feedback": "Updated feedback"
        }
        """;

        // Act
        var result = await NpsResponsesTools.UpdateNpsResponse(service, "nps-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated feedback");
    }

    [Fact]
    public async Task DeleteNpsResponse_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "NPS response deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await NpsResponsesTools.DeleteNpsResponse(service, "nps-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
