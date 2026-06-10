using FluentAssertions;
using Moq;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace VitallyMcp.Tests;

/// <summary>
/// Tests for VitallyService JSON filtering, field selection, trait filtering, and pagination.
/// </summary>
public class VitallyServiceTests
{
    private const string TestSubdomain = "test-subdomain";
    private const string TestApiKey = "sk_live_test_key";

    private VitallyService CreateService(HttpClient httpClient)
    {
        // Explicit US region so the subdomain-in-URL assertions below still hold.
        // EU is the default region; tests that exercise EU build their own options.
        return TestHelpers.BuildVitallyService(httpClient, region: "US", subdomain: TestSubdomain, apiKey: TestApiKey);
    }

    #region Default Field Tests

    [Fact]
    public async Task GetResourcesAsync_AccountsWithNoFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should include default fields
        firstAccount.TryGetProperty("id", out _).Should().BeTrue();
        firstAccount.TryGetProperty("name", out _).Should().BeTrue();
        firstAccount.TryGetProperty("createdAt", out _).Should().BeTrue();
        firstAccount.TryGetProperty("healthScore", out _).Should().BeTrue();
        firstAccount.TryGetProperty("mrr", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstAccount.TryGetProperty("traits", out _).Should().BeFalse();

        // Assert - Should NOT include large text fields
        firstAccount.TryGetProperty("largeTextField", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_OrganizationsWithNoFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleOrganizationJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("organizations", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstOrg = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should include default fields
        firstOrg.TryGetProperty("id", out _).Should().BeTrue();
        firstOrg.TryGetProperty("name", out _).Should().BeTrue();
        firstOrg.TryGetProperty("healthScore", out _).Should().BeTrue();
        firstOrg.TryGetProperty("mrr", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstOrg.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_UsersWithNoFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleUserJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("users", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstUser = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should include default fields
        firstUser.TryGetProperty("id", out _).Should().BeTrue();
        firstUser.TryGetProperty("name", out _).Should().BeTrue();
        firstUser.TryGetProperty("email", out _).Should().BeTrue();
        firstUser.TryGetProperty("accountId", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstUser.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithDefaultsKey_UsesThatKeysDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act — instance URL path is not an exact-match defaults key, so we pass defaultsKey explicitly
        var result = await service.GetResourcesAsync(
            "customObjects/cobj-1/instances", defaultsKey: "customObjectInstances");
        var jsonDoc = JsonDocument.Parse(result);
        var firstInstance = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert — new instance default fields present
        firstInstance.TryGetProperty("name", out _).Should().BeTrue();
        firstInstance.TryGetProperty("organizationId", out _).Should().BeTrue();
        firstInstance.TryGetProperty("customerId", out _).Should().BeTrue();

        // Assert — large field excluded by default
        firstInstance.TryGetProperty("descriptionBody", out _).Should().BeFalse();
    }

    #endregion

    #region Field Filtering Tests

    [Fact]
    public async Task GetResourcesAsync_WithSpecificFields_ShouldReturnOnlyRequestedFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request only id and name
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should only have id and name
        firstAccount.TryGetProperty("id", out _).Should().BeTrue();
        firstAccount.TryGetProperty("name", out _).Should().BeTrue();
        firstAccount.TryGetProperty("createdAt", out _).Should().BeFalse();
        firstAccount.TryGetProperty("healthScore", out _).Should().BeFalse();
        firstAccount.TryGetProperty("mrr", out _).Should().BeFalse();
        firstAccount.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithNonExistentFields_ShouldSkipThem()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request existing and non-existent fields
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,nonExistentField1,nonExistentField2");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should only have existing fields
        firstAccount.TryGetProperty("id", out _).Should().BeTrue();
        firstAccount.TryGetProperty("name", out _).Should().BeTrue();
        firstAccount.TryGetProperty("nonExistentField1", out _).Should().BeFalse();
        firstAccount.TryGetProperty("nonExistentField2", out _).Should().BeFalse();
    }

    #endregion

    #region Trait Filtering Tests

    [Fact]
    public async Task GetResourcesAsync_WithTraitsButNoTraitFilter_ShouldReturnAllTraits()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request traits field without specifying which traits
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,traits");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];
        var traits = firstAccount.GetProperty("traits");

        // Assert - Should include all traits from the response
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("paymentMethod", out _).Should().BeTrue();
        traits.TryGetProperty("plan", out _).Should().BeTrue();
        traits.TryGetProperty("employees", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetResourcesAsync_WithSpecificTraits_ShouldReturnOnlyRequestedTraits()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request only specific traits
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,traits", traits: "industry,paymentMethod");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];
        var traits = firstAccount.GetProperty("traits");

        // Assert - Should only have requested traits
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("paymentMethod", out _).Should().BeTrue();
        traits.TryGetProperty("plan", out _).Should().BeFalse();
        traits.TryGetProperty("employees", out _).Should().BeFalse();
        traits.TryGetProperty("customField1", out _).Should().BeFalse();
        traits.TryGetProperty("customField2", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithTraitsFieldButNoTraitsInResponse_ShouldNotIncludeTraits()
    {
        // Arrange - Response without traits object
        var jsonWithoutTraits = """
        {
          "results": [
            {
              "id": "account-123",
              "name": "Test Account"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(jsonWithoutTraits);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name,traits");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should not have traits property since it wasn't in original response
        firstAccount.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_WithNonExistentTraits_ShouldSkipThem()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act - Request traits that don't exist
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,traits", traits: "industry,nonExistentTrait1,nonExistentTrait2");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAccount = jsonDoc.RootElement.GetProperty("results")[0];
        var traits = firstAccount.GetProperty("traits");

        // Assert - Should only have existing traits
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("nonExistentTrait1", out _).Should().BeFalse();
        traits.TryGetProperty("nonExistentTrait2", out _).Should().BeFalse();
    }

    #endregion

    #region Pagination Tests

    [Fact]
    public async Task GetResourcesAsync_WithPagination_ShouldPreserveNextCursor()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Next cursor should be preserved
        jsonDoc.RootElement.TryGetProperty("next", out var nextProperty).Should().BeTrue();
        nextProperty.GetString().Should().Be("cursor-next-page");
    }

    [Fact]
    public async Task GetResourcesAsync_WithoutNextCursor_ShouldNotIncludeNext()
    {
        // Arrange - Response without next cursor
        var jsonWithoutNext = """
        {
          "results": [
            {
              "id": "account-123",
              "name": "Test Account"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(jsonWithoutNext);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Next should not be present
        jsonDoc.RootElement.TryGetProperty("next", out _).Should().BeFalse();
    }

    #endregion

    #region Empty Results Tests

    [Fact]
    public async Task GetResourcesAsync_WithEmptyResults_ShouldReturnEmptyArray()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetEmptyResultsJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("accounts", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var results = jsonDoc.RootElement.GetProperty("results");

        // Assert
        results.GetArrayLength().Should().Be(0);
    }

    #endregion

    #region GetResourceByIdAsync Tests

    [Fact]
    public async Task GetResourceByIdAsync_WithDefaultFields_ShouldReturnDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourceByIdAsync("accounts", "account-123");
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Should include default fields
        jsonDoc.RootElement.TryGetProperty("id", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("name", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("healthScore", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        jsonDoc.RootElement.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourceByIdAsync_WithSpecificFields_ShouldReturnOnlyRequestedFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourceByIdAsync("accounts", "account-123", fields: "id,name");
        var jsonDoc = JsonDocument.Parse(result);

        // Assert - Should only have requested fields
        jsonDoc.RootElement.TryGetProperty("id", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("name", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("healthScore", out _).Should().BeFalse();
        jsonDoc.RootElement.TryGetProperty("mrr", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourceByIdAsync_WithTraits_ShouldFilterTraitsCorrectly()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleAccountJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourceByIdAsync("accounts", "account-123", fields: "id,traits", traits: "industry");
        var jsonDoc = JsonDocument.Parse(result);
        var traits = jsonDoc.RootElement.GetProperty("traits");

        // Assert - Should only have industry trait
        traits.TryGetProperty("industry", out _).Should().BeTrue();
        traits.TryGetProperty("paymentMethod", out _).Should().BeFalse();
    }

    #endregion

    #region Multiple Resources Tests

    [Fact]
    public async Task GetResourcesAsync_WithMultipleResults_ShouldFilterAllResults()
    {
        // Arrange
        var multipleAccountsJson = """
        {
          "results": [
            {
              "id": "account-1",
              "name": "Account 1",
              "healthScore": 80,
              "mrr": 1000,
              "traits": { "industry": "Tech", "plan": "Pro" }
            },
            {
              "id": "account-2",
              "name": "Account 2",
              "healthScore": 90,
              "mrr": 2000,
              "traits": { "industry": "Finance", "plan": "Enterprise" }
            },
            {
              "id": "account-3",
              "name": "Account 3",
              "healthScore": 70,
              "mrr": 500,
              "traits": { "industry": "Healthcare", "plan": "Basic" }
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(multipleAccountsJson);
        var service = CreateService(mockClient);

        // Act - Request only id and name
        var result = await service.GetResourcesAsync("accounts", limit: 20, fields: "id,name");
        var jsonDoc = JsonDocument.Parse(result);
        var results = jsonDoc.RootElement.GetProperty("results");

        // Assert - All 3 results should be filtered
        results.GetArrayLength().Should().Be(3);

        foreach (var account in results.EnumerateArray())
        {
            account.TryGetProperty("id", out _).Should().BeTrue();
            account.TryGetProperty("name", out _).Should().BeTrue();
            account.TryGetProperty("healthScore", out _).Should().BeFalse();
            account.TryGetProperty("mrr", out _).Should().BeFalse();
            account.TryGetProperty("traits", out _).Should().BeFalse();
        }
    }

    #endregion

    #region Resource-Specific Default Field Tests

    [Fact]
    public async Task GetResourcesAsync_Tasks_ShouldHaveCorrectDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleTaskJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("tasks", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstTask = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should have task-specific default fields
        firstTask.TryGetProperty("id", out _).Should().BeTrue();
        firstTask.TryGetProperty("name", out _).Should().BeTrue();
        firstTask.TryGetProperty("dueDate", out _).Should().BeTrue();
        firstTask.TryGetProperty("completedAt", out _).Should().BeTrue();
        firstTask.TryGetProperty("assignedToId", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstTask.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_Notes_ShouldHaveCorrectDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleNoteJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("notes", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstNote = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should have note-specific default fields
        firstNote.TryGetProperty("id", out _).Should().BeTrue();
        firstNote.TryGetProperty("subject", out _).Should().BeTrue();
        firstNote.TryGetProperty("noteDate", out _).Should().BeTrue();
        firstNote.TryGetProperty("authorId", out _).Should().BeTrue();
        firstNote.TryGetProperty("categoryId", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstNote.TryGetProperty("traits", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_Projects_ShouldHaveCorrectDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleProjectJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("projects", limit: 20);
        var jsonDoc = JsonDocument.Parse(result);
        var firstProject = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - Should have project-specific default fields
        firstProject.TryGetProperty("id", out _).Should().BeTrue();
        firstProject.TryGetProperty("name", out _).Should().BeTrue();
        firstProject.TryGetProperty("accountId", out _).Should().BeTrue();
        firstProject.TryGetProperty("organizationId", out _).Should().BeTrue();
        firstProject.TryGetProperty("archivedAt", out _).Should().BeTrue();

        // Assert - Should NOT include traits by default
        firstProject.TryGetProperty("traits", out _).Should().BeFalse();
    }

    #endregion

    #region CreateResourceAsync Tests

    [Fact]
    public async Task CreateResourceAsync_ShouldPostJsonBodyToResourcePath()
    {
        // Arrange
        var responseJson = """{"id":"acc-new","name":"New Account"}""";
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(responseJson);
        var service = CreateService(client);

        // Act
        var result = await service.CreateResourceAsync("accounts", """{"name":"New Account"}""");

        // Assert - request shape
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath == "/resources/accounts"),
            ItExpr.IsAny<CancellationToken>());

        // Assert - response passes through unfiltered
        result.Should().Be(responseJson);
    }

    [Fact]
    public async Task CreateResourceAsync_ShouldSendJsonContentType()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"id":"x"}""");
        var service = CreateService(client);

        // Act
        await service.CreateResourceAsync("notes", """{"subject":"Hello"}""");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Content!.Headers.ContentType!.MediaType == "application/json"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task CreateResourceAsync_OnNonSuccessStatus_ShouldThrow()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("error", HttpStatusCode.BadRequest);
        var service = CreateService(client);

        // Act
        var act = () => service.CreateResourceAsync("accounts", "{}");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SendAsync_OnNonSuccessStatus_ExceptionMessageIncludesResponseBody()
    {
        // Vitally returns the real failure reason in the response body (e.g. "externalId is
        // required") - this must reach the caller so the LLM can react. Don't regress.
        var client = TestHelpers.CreateMockHttpClient(
            """{"message":"externalId is required"}""",
            HttpStatusCode.BadRequest);
        var service = CreateService(client);

        var act = () => service.CreateResourceAsync("accounts", "{}");

        var exception = await act.Should().ThrowAsync<HttpRequestException>();
        exception.Which.Message.Should().Contain("externalId is required");
        exception.Which.Message.Should().Contain("400");
        exception.Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region UpdateResourceAsync Tests

    [Fact]
    public async Task UpdateResourceAsync_ShouldPutJsonBodyToResourceIdPath()
    {
        // Arrange
        var responseJson = """{"id":"acc-123","name":"Renamed"}""";
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(responseJson);
        var service = CreateService(client);

        // Act
        var result = await service.UpdateResourceAsync("accounts", "acc-123", """{"name":"Renamed"}""");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Put
                && req.RequestUri!.AbsolutePath == "/resources/accounts/acc-123"),
            ItExpr.IsAny<CancellationToken>());

        result.Should().Be(responseJson);
    }

    [Fact]
    public async Task UpdateResourceAsync_OnNonSuccessStatus_ShouldThrow()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("not found", HttpStatusCode.NotFound);
        var service = CreateService(client);

        // Act
        var act = () => service.UpdateResourceAsync("accounts", "missing", "{}");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region DeleteResourceAsync Tests

    [Fact]
    public async Task DeleteResourceAsync_ShouldSendDeleteToResourceIdPath()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("");
        var service = CreateService(client);

        // Act
        await service.DeleteResourceAsync("accounts", "acc-123");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Delete
                && req.RequestUri!.AbsolutePath == "/resources/accounts/acc-123"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DeleteResourceAsync_OnNonSuccessStatus_ShouldThrow()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("forbidden", HttpStatusCode.Forbidden);
        var service = CreateService(client);

        // Act
        var act = () => service.DeleteResourceAsync("accounts", "acc-123");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region GetRawAsync Tests

    [Fact]
    public async Task GetRawAsync_WithNoQueryParams_ShouldGetUnfilteredBody()
    {
        // Arrange - raw body uses 'data' envelope (not the standard 'results' one)
        var rawJson = """{"data":[{"id":"sr-1","score":9}],"next":null}""";
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(rawJson);
        var service = CreateService(client);

        // Act
        var result = await service.GetRawAsync("surveys/survey-1/responses");

        // Assert - request URL is correct
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath == "/resources/surveys/survey-1/responses"
                && string.IsNullOrEmpty(req.RequestUri.Query)),
            ItExpr.IsAny<CancellationToken>());

        // Assert - body passes through unfiltered (no field stripping)
        result.Should().Be(rawJson);
    }

    [Fact]
    public async Task GetRawAsync_WithQueryParams_ShouldAppendUrlEncodedQuery()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("[]");
        var service = CreateService(client);

        // Act
        await service.GetRawAsync("customFields", new Dictionary<string, string>
        {
            ["model"] = "customObjects",
            ["customObjectId"] = "obj 123"
        });

        // Assert - both params are present and the space in 'obj 123' is URL-escaped
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath == "/resources/customFields"
                && req.RequestUri.Query.Contains("model=customObjects")
                && req.RequestUri.Query.Contains("customObjectId=obj%20123")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetRawAsync_WithEmptyQueryParamsDictionary_ShouldNotAddQueryString()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("{}");
        var service = CreateService(client);

        // Act
        await service.GetRawAsync("anything", new Dictionary<string, string>());

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => string.IsNullOrEmpty(req.RequestUri!.Query)),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetRawAsync_OnNonSuccessStatus_ShouldThrow()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("oops", HttpStatusCode.InternalServerError);
        var service = CreateService(client);

        // Act
        var act = () => service.GetRawAsync("surveys/x/responses");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region PostRawAsync Tests

    [Fact]
    public async Task PostRawAsync_ShouldPostJsonToArbitraryPath()
    {
        // Arrange - meeting participants is a sub-resource
        var responseJson = """{"id":"meet-1","participants":[{"id":"p-99"}]}""";
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(responseJson);
        var service = CreateService(client);

        // Act
        var result = await service.PostRawAsync(
            "meetings/meet-1/participants",
            """{"email":"a@b.com","type":"attendee"}""");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath == "/resources/meetings/meet-1/participants"
                && req.Content!.Headers.ContentType!.MediaType == "application/json"),
            ItExpr.IsAny<CancellationToken>());

        result.Should().Be(responseJson);
    }

    [Fact]
    public async Task PostRawAsync_OnNonSuccessStatus_ShouldThrow()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("bad", HttpStatusCode.UnprocessableEntity);
        var service = CreateService(client);

        // Act
        var act = () => service.PostRawAsync("meetings/x/participants", "{}");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region DeleteRawAsync Tests

    [Fact]
    public async Task DeleteRawAsync_ShouldSendDeleteToArbitraryPath()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("");
        var service = CreateService(client);

        // Act
        await service.DeleteRawAsync("meetings/meet-1/participants/p-99");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Delete
                && req.RequestUri!.AbsolutePath == "/resources/meetings/meet-1/participants/p-99"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_OnNonSuccessStatus_ShouldThrow()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("nope", HttpStatusCode.NotFound);
        var service = CreateService(client);

        // Act
        var act = () => service.DeleteRawAsync("meetings/x/participants/y");

        // Assert
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    #endregion

    #region Authentication Header Tests

    [Fact]
    public async Task GetResourcesAsync_ShouldSendBasicAuthHeader()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);

        // Act
        await service.GetResourcesAsync("accounts");

        // Assert - Basic auth using base64("apiKey:")
        var expectedToken = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{TestApiKey}:"));
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Headers.Authorization!.Scheme == "Basic"
                && req.Headers.Authorization.Parameter == expectedToken),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetResourcesAsync_ShouldUseSubdomainInBaseUrl()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);

        // Act
        await service.GetResourcesAsync("accounts");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.Host == $"{TestSubdomain}.rest.vitally.io"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetResourcesAsync_WithRegionEu_ShouldUseEuHostNotSubdomain()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = TestHelpers.BuildVitallyService(client, region: "EU", subdomain: "ignored-on-eu", apiKey: TestApiKey);

        // Act
        await service.GetResourcesAsync("accounts");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.Host == "rest.vitally-eu.io"
                && req.RequestUri.AbsolutePath == "/resources/accounts"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetResourcesAsync_WithRegionUs_ShouldUseSubdomainHost()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = TestHelpers.BuildVitallyService(client, region: "US", subdomain: "tenant-x", apiKey: TestApiKey);

        // Act
        await service.GetResourcesAsync("accounts");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.Host == "tenant-x.rest.vitally.io"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetResourcesAsync_ShouldUrlEncodeQueryParameters()
    {
        // Arrange — values contain a space, an ampersand, an equals sign, and a slash,
        // each of which would corrupt the URL if concatenated verbatim.
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);
        var additionalParams = new Dictionary<string, string>
        {
            ["customFieldValue"] = "Acme Corp",        // space
            ["filter"] = "name=John&Co",                // & and =
            ["path"] = "a/b"                            // slash
        };

        // Act
        await service.GetResourcesAsync(
            "customObjects/inst/instances/search",
            limit: 10,
            from: "cursor with space",
            sortBy: "createdAt desc",
            additionalParams: additionalParams);

        // Assert — every value in the resulting URL is percent-encoded.
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.Query.Contains("from=cursor%20with%20space")
                && req.RequestUri.Query.Contains("sortBy=createdAt%20desc")
                && req.RequestUri.Query.Contains("customFieldValue=Acme%20Corp")
                && req.RequestUri.Query.Contains("filter=name%3DJohn%26Co")
                && req.RequestUri.Query.Contains("path=a%2Fb")),
            ItExpr.IsAny<CancellationToken>());
    }

    #endregion

    #region SearchCustomObjectInstancesAsync Tests

    [Fact]
    public async Task SearchCustomObjectInstancesAsync_BuildsSearchUrlWithCriterionAndNoLimit()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);
        var criteria = new Dictionary<string, string> { ["organizationId"] = "org-456" };

        // Act
        var result = await service.SearchCustomObjectInstancesAsync("cobj-123", criteria);

        // Assert — routes to /search with the criterion and NO limit param
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("organizationId=org-456")
                && !req.RequestUri.Query.Contains("limit")),
            ItExpr.IsAny<CancellationToken>());

        // Assert — list envelope is filtered and preserved
        result.Should().Contain("\"results\"");
        result.Should().Contain("\"organizationId\"");
    }

    #endregion

    #region Resource-Specific Defaults — newly-added resources

    [Fact]
    public async Task GetResourcesAsync_AdminsSearch_ShouldHaveAdminDefaultFields()
    {
        // Arrange - admins/search is the resource path used by AdminsTools.SearchAdmins
        var adminsSearchJson = """
        {
          "results": [
            {
              "id": "admin-1",
              "name": "Admin User",
              "email": "admin@example.com",
              "createdAt": "2024-01-01T00:00:00Z",
              "extraField": "should be filtered out"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(adminsSearchJson);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("admins/search");
        var jsonDoc = JsonDocument.Parse(result);
        var firstAdmin = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - has the admin defaults, not the bare fallback
        firstAdmin.TryGetProperty("id", out _).Should().BeTrue();
        firstAdmin.TryGetProperty("name", out _).Should().BeTrue();
        firstAdmin.TryGetProperty("email", out _).Should().BeTrue();
        firstAdmin.TryGetProperty("extraField", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_MeetingsWithNoFields_ShouldReturnMeetingDefaultFields()
    {
        // Arrange
        var meetingsJson = """
        {
          "results": [
            {
              "id": "meet-1",
              "title": "Quarterly review",
              "externalId": "ext-meet-1",
              "startDateTime": "2024-03-10T14:00:00Z",
              "endDateTime": "2024-03-10T15:00:00Z",
              "location": "Zoom",
              "source": "calendar",
              "summary": "long summary text we don't want by default",
              "transcript": "very long transcript we don't want by default"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(meetingsJson);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("meetings");
        var jsonDoc = JsonDocument.Parse(result);
        var firstMeeting = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert - core meeting fields present
        firstMeeting.TryGetProperty("id", out _).Should().BeTrue();
        firstMeeting.TryGetProperty("title", out _).Should().BeTrue();
        firstMeeting.TryGetProperty("startDateTime", out _).Should().BeTrue();
        firstMeeting.TryGetProperty("endDateTime", out _).Should().BeTrue();
        firstMeeting.TryGetProperty("location", out _).Should().BeTrue();
        firstMeeting.TryGetProperty("source", out _).Should().BeTrue();

        // Assert - heavy text fields excluded by default
        firstMeeting.TryGetProperty("summary", out _).Should().BeFalse();
        firstMeeting.TryGetProperty("transcript", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetResourcesAsync_MeetingTranscriptsWithNoFields_ShouldReturnTranscriptDefaultFields()
    {
        // Arrange
        var transcriptsJson = """
        {
          "results": [
            {
              "id": "trans-1",
              "meetingId": "meet-1",
              "createdAt": "2024-03-10T15:30:00Z",
              "updatedAt": "2024-03-10T15:30:00Z",
              "transcript": "very long transcript text we don't want by default"
            }
          ]
        }
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(transcriptsJson);
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetResourcesAsync("meetingTranscripts");
        var jsonDoc = JsonDocument.Parse(result);
        var firstTranscript = jsonDoc.RootElement.GetProperty("results")[0];

        // Assert
        firstTranscript.TryGetProperty("id", out _).Should().BeTrue();
        firstTranscript.TryGetProperty("meetingId", out _).Should().BeTrue();
        firstTranscript.TryGetProperty("createdAt", out _).Should().BeTrue();
        firstTranscript.TryGetProperty("updatedAt", out _).Should().BeTrue();

        // Heavy transcript text excluded by default
        firstTranscript.TryGetProperty("transcript", out _).Should().BeFalse();
    }

    #endregion
}
