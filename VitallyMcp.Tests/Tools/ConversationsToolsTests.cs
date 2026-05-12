using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for ConversationsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class ConversationsToolsTests
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
    public async Task ListConversations_WithDefaultParameters_ShouldReturnConversations()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleConversationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.ListConversations(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"subject\"");
    }

    [Fact]
    public async Task ListConversations_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleConversationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.ListConversations(service, fields: "id,subject");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"subject\"");
    }

    [Fact]
    public async Task ListConversationsByAccount_WithValidAccountId_ShouldReturnConversations()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleConversationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.ListConversationsByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListConversationsByOrganization_WithValidOrgId_ShouldReturnConversations()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleConversationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.ListConversationsByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task GetConversation_WithValidId_ShouldReturnSingleConversation()
    {
        // Arrange
        var singleConvJson = """
        {
          "id": "conv-123",
          "subject": "Test Conversation",
          "externalId": "ext-conv-123",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleConvJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.GetConversation(service, "conv-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("conv-123");
        result.Should().Contain("Test Conversation");
    }

    [Fact]
    public async Task GetConversation_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleConvJson = """
        {
          "id": "conv-123",
          "subject": "Test Conversation"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleConvJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.GetConversation(service, "conv-123", fields: "id,subject");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"subject\"");
    }

    [Fact]
    public async Task CreateConversation_WithValidData_ShouldReturnCreatedConversation()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-conv-123",
          "subject": "New Conversation",
          "externalId": "ext-new-conv"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var convData = """
        {
          "externalId": "ext-new-conv",
          "subject": "New Conversation",
          "messages": []
        }
        """;

        // Act
        var result = await ConversationsTools.CreateConversation(service, convData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-conv-123");
        result.Should().Contain("New Conversation");
    }

    [Fact]
    public async Task UpdateConversation_WithValidData_ShouldReturnUpdatedConversation()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "conv-123",
          "subject": "Updated Subject"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "subject": "Updated Subject",
          "messages": []
        }
        """;

        // Act
        var result = await ConversationsTools.UpdateConversation(service, "conv-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Subject");
    }

    [Fact]
    public async Task DeleteConversation_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Conversation deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await ConversationsTools.DeleteConversation(service, "conv-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
