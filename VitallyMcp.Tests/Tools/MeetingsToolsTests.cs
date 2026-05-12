using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for MeetingsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class MeetingsToolsTests
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
    public async Task ListMeetings_WithDefaultParameters_ShouldReturnMeetings()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetings(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"title\"");
    }

    [Fact]
    public async Task ListMeetings_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetings(service, fields: "id,title");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"title\"");
    }

    [Fact]
    public async Task ListMeetings_WithArchivedFilter_ShouldReturnMeetings()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetings(service, archived: "true");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
    }

    [Fact]
    public async Task ListMeetings_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetings(service, fields: "id,title,traits", traits: "topic");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"topic\"");
    }

    [Fact]
    public async Task ListMeetingsByAccount_WithValidAccountId_ShouldReturnMeetings()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetingsByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListMeetingsByOrganization_WithValidOrgId_ShouldReturnMeetings()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetingsByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task GetMeeting_WithValidId_ShouldReturnSingleMeeting()
    {
        // Arrange
        var singleMtgJson = """
        {
          "id": "mtg-123",
          "title": "Quarterly Review",
          "externalId": "ext-mtg-123"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleMtgJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.GetMeeting(service, "mtg-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("mtg-123");
        result.Should().Contain("Quarterly Review");
    }

    [Fact]
    public async Task GetMeeting_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleMtgJson = """
        {
          "id": "mtg-123",
          "title": "Quarterly Review"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleMtgJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.GetMeeting(service, "mtg-123", fields: "id,title");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"title\"");
    }

    [Fact]
    public async Task CreateMeeting_WithValidData_ShouldReturnCreatedMeeting()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-mtg-123",
          "title": "New Meeting",
          "externalId": "ext-new-mtg"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var meetingData = """
        {
          "title": "New Meeting",
          "externalId": "ext-new-mtg",
          "participants": []
        }
        """;

        // Act
        var result = await MeetingsTools.CreateMeeting(service, meetingData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-mtg-123");
        result.Should().Contain("New Meeting");
    }

    [Fact]
    public async Task UpdateMeeting_WithValidData_ShouldReturnUpdatedMeeting()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "mtg-123",
          "title": "Updated Title"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "title": "Updated Title"
        }
        """;

        // Act
        var result = await MeetingsTools.UpdateMeeting(service, "mtg-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Title");
    }

    [Fact]
    public async Task DeleteMeeting_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Meeting archived successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.DeleteMeeting(service, "mtg-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }

    [Fact]
    public async Task AddMeetingParticipant_WithValidData_ShouldReturnCreatedParticipant()
    {
        // Arrange - PostRawAsync returns the raw body unchanged
        var createdJson = """
        {
          "id": "part-new-123",
          "userId": "user-456",
          "type": "attendee"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var participantData = """
        {
          "userId": "user-456",
          "type": "attendee"
        }
        """;

        // Act
        var result = await MeetingsTools.AddMeetingParticipant(service, "mtg-123", participantData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("part-new-123");
        result.Should().Contain("attendee");
    }

    [Fact]
    public async Task RemoveMeetingParticipant_WithValidIds_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Participant removed"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.RemoveMeetingParticipant(service, "mtg-123", "part-456");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }

    [Fact]
    public async Task ListMeetingTranscripts_WithDefaultParameters_ShouldReturnTranscripts()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleMeetingTranscriptJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.ListMeetingTranscripts(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
        result.Should().Contain("trans-123");
    }

    [Fact]
    public async Task GetMeetingTranscript_WithValidId_ShouldReturnSingleTranscript()
    {
        // Arrange
        var singleTranscriptJson = """
        {
          "id": "trans-123",
          "meetingId": "mtg-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleTranscriptJson);
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.GetMeetingTranscript(service, "trans-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("trans-123");
    }

    [Fact]
    public async Task GetTranscriptForMeeting_WithValidMeetingId_ShouldReturnRawTranscript()
    {
        // Arrange - GetRawAsync returns the raw body unchanged (no field filtering envelope)
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRawTranscriptJson());
        var service = CreateService(mockClient);

        // Act
        var result = await MeetingsTools.GetTranscriptForMeeting(service, "mtg-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("trans-123");
        result.Should().Contain("speaker-1");
        result.Should().Contain("Hello everyone");
    }

    [Fact]
    public async Task CreateMeetingTranscript_WithValidData_ShouldReturnCreatedTranscript()
    {
        // Arrange - PostRawAsync returns the raw body unchanged
        var createdJson = """
        {
          "id": "trans-new-123",
          "meetingId": "mtg-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var transcriptData = """
        {
          "transcript": [
            {
              "speaker": { "externalId": "speaker-1" },
              "sentences": [{ "text": "Hi", "startTime": 0.0, "endTime": 1.0 }]
            }
          ]
        }
        """;

        // Act
        var result = await MeetingsTools.CreateMeetingTranscript(service, "mtg-123", transcriptData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("trans-new-123");
    }
}
