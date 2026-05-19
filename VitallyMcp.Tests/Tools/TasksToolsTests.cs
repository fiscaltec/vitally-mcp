using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for TasksTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class TasksToolsTests
{
    private const string TestSubdomain = "test-subdomain";
    private const string TestApiKey = "sk_live_test_key";

    private VitallyService CreateService(HttpClient httpClient)
    {
        return TestHelpers.BuildVitallyService(httpClient, subdomain: TestSubdomain, apiKey: TestApiKey);

    }

    [Fact]
    public async Task ListTasks_WithDefaultParameters_ShouldReturnTasks()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.ListTasks(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListTasks_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.ListTasks(service, fields: "id,name,dueDate");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
        result.Should().Contain("\"dueDate\"");
    }

    [Fact]
    public async Task ListTasks_WithTraits_ShouldIncludeTraitsInResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.ListTasks(service, fields: "id,name,traits", traits: "priority");

        // Assert
        result.Should().Contain("\"traits\"");
        result.Should().Contain("\"priority\"");
    }

    [Fact]
    public async Task ListTasksByAccount_WithValidAccountId_ShouldReturnTasks()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.ListTasksByAccount(service, "account-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListTasksByOrganization_WithValidOrgId_ShouldReturnTasks()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.ListTasksByOrganization(service, "org-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListTaskCategories_WithDefaultParameters_ShouldReturnCategories()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskCategoryJson());
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.ListTaskCategories(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task GetTask_WithValidId_ShouldReturnSingleTask()
    {
        // Arrange
        var singleTaskJson = """
        {
          "id": "task-123",
          "name": "Test Task",
          "dueDate": "2024-02-01",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleTaskJson);
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.GetTask(service, "task-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("task-123");
        result.Should().Contain("Test Task");
    }

    [Fact]
    public async Task GetTask_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var singleTaskJson = """
        {
          "id": "task-123",
          "name": "Test Task"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleTaskJson);
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.GetTask(service, "task-123", fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task CreateTask_WithValidData_ShouldReturnCreatedTask()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-task-123",
          "name": "New Task",
          "accountId": "account-456"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var taskData = """
        {
          "name": "New Task",
          "accountId": "account-456"
        }
        """;

        // Act
        var result = await TasksTools.CreateTask(service, taskData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-task-123");
        result.Should().Contain("New Task");
    }

    [Fact]
    public async Task UpdateTask_WithValidData_ShouldReturnUpdatedTask()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "task-123",
          "name": "Updated Task Name"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "name": "Updated Task Name"
        }
        """;

        // Act
        var result = await TasksTools.UpdateTask(service, "task-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Task Name");
    }

    [Fact]
    public async Task DeleteTask_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Task deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await TasksTools.DeleteTask(service, "task-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }
}
