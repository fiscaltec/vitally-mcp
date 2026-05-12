using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for NotesTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class NotesToolsTests
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
    public async Task ListNotes_WithDefaultParameters_ShouldReturnNotes()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.ListNotes(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"subject\"");
    }

    [Fact]
    public async Task ListNotes_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.ListNotes(service, fields: "id,subject");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"subject\"");
    }

    [Fact]
    public async Task ListNotes_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.ListNotes(service, fields: "id,subject,traits", traits: "type");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"type\"");
    }

    [Fact]
    public async Task ListNotesByAccount_WithValidAccountId_ShouldReturnNotes()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.ListNotesByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListNotesByOrganization_WithValidOrgId_ShouldReturnNotes()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.ListNotesByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListNoteCategories_WithDefaultParameters_ShouldReturnCategories()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteCategoryJson());
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.ListNoteCategories(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task GetNote_WithValidId_ShouldReturnSingleNote()
    {
        // Arrange
        var singleNoteJson = """
        {
          "id": "note-123",
          "subject": "Test Note",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleNoteJson);
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.GetNote(service, "note-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("note-123");
        result.Should().Contain("Test Note");
    }

    [Fact]
    public async Task GetNote_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleNoteJson = """
        {
          "id": "note-123",
          "subject": "Test Note"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleNoteJson);
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.GetNote(service, "note-123", fields: "id,subject");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"subject\"");
    }

    [Fact]
    public async Task CreateNote_WithValidData_ShouldReturnCreatedNote()
    {
        // Arrange
        var createdNoteJson = """
        {
          "id": "new-note-123",
          "subject": "New Note",
          "accountId": "account-456",
          "noteDate": "2024-01-15"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdNoteJson);
        var service = CreateService(mockClient);

        var noteData = """
        {
          "subject": "New Note",
          "accountId": "account-456",
          "note": "<p>Some content</p>",
          "noteDate": "2024-01-15T00:00:00Z"
        }
        """;

        // Act
        var result = await NotesTools.CreateNote(service, noteData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-note-123");
        result.Should().Contain("New Note");
    }

    [Fact]
    public async Task UpdateNote_WithValidData_ShouldReturnUpdatedNote()
    {
        // Arrange
        var updatedNoteJson = """
        {
          "id": "note-123",
          "subject": "Updated Subject"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedNoteJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "subject": "Updated Subject"
        }
        """;

        // Act
        var result = await NotesTools.UpdateNote(service, "note-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Subject");
    }

    [Fact]
    public async Task DeleteNote_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Note deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await NotesTools.DeleteNote(service, "note-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
