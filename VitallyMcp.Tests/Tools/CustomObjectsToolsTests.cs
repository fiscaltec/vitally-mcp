using FluentAssertions;
using Moq;
using Moq.Protected;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for CustomObjectsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class CustomObjectsToolsTests
{
    private const string TestSubdomain = "test-subdomain";
    private const string TestApiKey = "sk_live_test_key";

    private VitallyService CreateService(HttpClient httpClient)
    {
        return TestHelpers.BuildVitallyService(httpClient, subdomain: TestSubdomain, apiKey: TestApiKey);

    }

    [Fact]
    public async Task ListCustomObjects_WithDefaultParameters_ShouldReturnObjects()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleCustomObjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.ListCustomObjects(service);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task ListCustomObjects_WithFieldFilter_ShouldReturnFilteredFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleCustomObjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.ListCustomObjects(service, fields: "id,name");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"id\"");
        result.Should().Contain("\"name\"");
    }

    [Fact]
    public async Task GetCustomObject_WithValidId_ShouldReturnSingleObject()
    {
        // Arrange
        var singleObjJson = """
        {
          "id": "cobj-123",
          "name": "Subscription"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(singleObjJson);
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.GetCustomObject(service, "cobj-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("cobj-123");
        result.Should().Contain("Subscription");
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithValidObjectId_ShouldReturnInstances()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"results\"");
        result.Should().Contain("inst-123");
    }

    [Fact]
    public async Task SearchCustomObjectInstances_WithQuery_ShouldReturnMatchingInstances()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.SearchCustomObjectInstances(service, "cobj-123", "externalId=ext-inst-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("inst-123");
    }

    [Fact]
    public async Task CreateCustomObject_WithValidData_ShouldReturnCreatedObject()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-cobj-123",
          "name": "New Object"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var data = """
        {
          "name": "New Object"
        }
        """;

        // Act
        var result = await CustomObjectsTools.CreateCustomObject(service, data);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-cobj-123");
        result.Should().Contain("New Object");
    }

    [Fact]
    public async Task UpdateCustomObject_WithValidData_ShouldReturnUpdatedObject()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "cobj-123",
          "name": "Updated Name"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "name": "Updated Name"
        }
        """;

        // Act
        var result = await CustomObjectsTools.UpdateCustomObject(service, "cobj-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Updated Name");
    }

    [Fact]
    public async Task CreateCustomObjectInstance_WithValidData_ShouldReturnCreatedInstance()
    {
        // Arrange
        var createdJson = """
        {
          "id": "new-inst-123",
          "externalId": "ext-new-inst"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(createdJson);
        var service = CreateService(mockClient);

        var data = """
        {
          "externalId": "ext-new-inst"
        }
        """;

        // Act
        var result = await CustomObjectsTools.CreateCustomObjectInstance(service, "cobj-123", data);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("new-inst-123");
    }

    [Fact]
    public async Task UpdateCustomObjectInstance_WithValidData_ShouldReturnUpdatedInstance()
    {
        // Arrange
        var updatedJson = """
        {
          "id": "inst-123",
          "externalId": "ext-inst-updated"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(updatedJson);
        var service = CreateService(mockClient);

        var updateData = """
        {
          "externalId": "ext-inst-updated"
        }
        """;

        // Act
        var result = await CustomObjectsTools.UpdateCustomObjectInstance(service, "cobj-123", "inst-123", updateData);

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("ext-inst-updated");
    }

    [Fact]
    public async Task DeleteCustomObjectInstance_WithValidId_ShouldReturnSuccessMessage()
    {
        // Arrange
        var deleteResponseJson = """
        {
          "success": true,
          "message": "Instance deleted successfully"
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(deleteResponseJson);
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.DeleteCustomObjectInstance(service, "cobj-123", "inst-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("success");
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithOrganizationId_RoutesToSearchWithoutLimit()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        var result = await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123", organizationId: "org-456");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("organizationId=org-456")
                && !req.RequestUri.Query.Contains("limit")),
            ItExpr.IsAny<CancellationToken>());
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithCustomFieldPair_RoutesToSearchWithBothParams()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        await CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", customFieldId: "cf-1", customFieldValue: "Gold");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("customFieldId=cf-1")
                && req.RequestUri.Query.Contains("customFieldValue=Gold")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithTwoScopeCriteria_ThrowsAndMakesNoCall()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);

        // Act
        Func<Task> act = () => CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", organizationId: "org-1", customerId: "cust-1");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithCustomFieldIdOnly_Throws()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);

        // Act
        Func<Task> act = () => CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", customFieldId: "cf-1");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithCustomerId_RoutesToSearch()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123", customerId: "cust-1");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("customerId=cust-1")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithCustomFieldValueOnly_Throws()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);

        // Act
        Func<Task> act = () => CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", customFieldValue: "Gold");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_Unscoped_SendsLimitToPlainListPath()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances"
                && req.RequestUri.Query.Contains("limit=20")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_Unscoped_AppliesInstanceDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123");

        // Assert — new defaults applied, large field excluded
        result.Should().Contain("\"organizationId\"");
        result.Should().Contain("\"name\"");
        result.Should().NotContain("descriptionBody");
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithTraits_SubsetsTraits()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act — request only the 'target' trait
        var result = await CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", fields: "id,traits", traits: "target");

        // Assert — only the requested trait survives
        result.Should().Contain("target");
        result.Should().NotContain("owner");
    }
}
