using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for MessagesTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class MessagesToolsTests
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
    public async Task ListMessagesByConversation_WithValidConversationId_ShouldReturnMessages()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMessageJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MessagesTools.ListMessagesByConversation(service, "conv-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
        result.Should().Contain("\"id\"");
    }

    [Fact]
    public async Task ListMessagesByConversation_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMessageJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MessagesTools.ListMessagesByConversation(service, "conv-123", fields: "id,message");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"message\"");
    }

    [Fact]
    public async Task GetMessage_WithValidId_ShouldReturnSingleMessage()
    {
        // Arrange
        var singleMsgJson = """
        {
          "id": "msg-123",
          "type": "email",
          "message": "Hello",
          "from": "sender@example.com",
          "to": "recipient@example.com",
          "timestamp": "2024-01-15T12:00:00Z"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleMsgJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MessagesTools.GetMessage(service, "msg-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("msg-123");
        result.Should().Contain("Hello");
    }

    [Fact]
    public async Task GetMessage_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleMsgJson = """
        {
          "id": "msg-123",
          "message": "Hello"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleMsgJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MessagesTools.GetMessage(service, "msg-123", fields: "id,message");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"message\"");
    }

    [Fact]
    public async Task CreateMessage_WithValidData_ShouldReturnCreatedMessage()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-msg-123",
          "externalId": "ext-msg-new",
          "message": "New message",
          "from": "sender@example.com",
          "to": "recipient@example.com"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var msgData = """
        {
          "externalId": "ext-msg-new",
          "message": "New message",
          "from": "sender@example.com",
          "to": "recipient@example.com"
        }
        """;

        // Act
        var result = await MessagesTools.CreateMessage(service, "conv-123", msgData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-msg-123");
        result.Should().Contain("New message");
    }

    [Fact]
    public async Task DeleteMessage_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Message deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MessagesTools.DeleteMessage(service, "msg-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
