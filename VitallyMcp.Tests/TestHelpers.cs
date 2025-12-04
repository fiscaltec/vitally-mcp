using System.Net;
using Moq;
using Moq.Protected;

namespace VitallyMcp.Tests;

/// <summary>
/// Helper utilities for creating mocks and test data.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Creates a mock HttpClient that returns the specified JSON response.
    /// </summary>
    public static HttpClient CreateMockHttpClient(string jsonResponse, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(jsonResponse)
            });

        return new HttpClient(mockHttpMessageHandler.Object);
    }

    /// <summary>
    /// Creates a mock HttpClient that allows verification of the request URL.
    /// </summary>
    public static (HttpClient client, Mock<HttpMessageHandler> handler) CreateMockHttpClientWithHandler(
        string jsonResponse,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var mockHttpMessageHandler = new Mock<HttpMessageHandler>();
        mockHttpMessageHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(jsonResponse)
            });

        var client = new HttpClient(mockHttpMessageHandler.Object);
        return (client, mockHttpMessageHandler);
    }

    /// <summary>
    /// Sample account JSON with all fields for testing filtering.
    /// </summary>
    public static string GetSampleAccountJson() => """
    {
      "results": [
        {
          "id": "account-123",
          "name": "Test Account",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "externalId": "ext-123",
          "organizationId": "org-456",
          "healthScore": 85.5,
          "mrr": 5000,
          "accountOwnerId": "owner-789",
          "lastSeenTimestamp": "2024-01-15T12:00:00Z",
          "status": "active",
          "description": "This is a test account description",
          "traits": {
            "industry": "Technology",
            "paymentMethod": "Credit Card",
            "plan": "Enterprise",
            "employees": 250,
            "customField1": "value1",
            "customField2": "value2"
          },
          "largeTextField": "This is a very large text field that we don't want to include by default to reduce response size"
        }
      ],
      "next": "cursor-next-page"
    }
    """;

    /// <summary>
    /// Sample organization JSON for testing.
    /// </summary>
    public static string GetSampleOrganizationJson() => """
    {
      "results": [
        {
          "id": "org-123",
          "name": "Test Organization",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "externalId": "ext-org-123",
          "healthScore": 90.0,
          "mrr": 15000,
          "lastSeenTimestamp": "2024-01-15T12:00:00Z",
          "traits": {
            "industry": "Finance",
            "region": "EMEA",
            "size": "Large"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Sample user JSON for testing.
    /// </summary>
    public static string GetSampleUserJson() => """
    {
      "results": [
        {
          "id": "user-123",
          "name": "John Doe",
          "email": "john.doe@example.com",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "externalId": "ext-user-123",
          "accountId": "account-456",
          "organizationId": "org-789",
          "lastSeenTimestamp": "2024-01-15T12:00:00Z",
          "traits": {
            "role": "Admin",
            "department": "Engineering"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Sample task JSON for testing.
    /// </summary>
    public static string GetSampleTaskJson() => """
    {
      "results": [
        {
          "id": "task-123",
          "name": "Test Task",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "externalId": "ext-task-123",
          "dueDate": "2024-02-01",
          "completedAt": null,
          "assignedToId": "user-456",
          "accountId": "account-789",
          "organizationId": "org-101",
          "archivedAt": null,
          "traits": {
            "priority": "High",
            "category": "Onboarding"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Sample note JSON for testing.
    /// </summary>
    public static string GetSampleNoteJson() => """
    {
      "results": [
        {
          "id": "note-123",
          "subject": "Test Note",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "externalId": "ext-note-123",
          "noteDate": "2024-01-15",
          "authorId": "user-456",
          "accountId": "account-789",
          "organizationId": "org-101",
          "categoryId": "cat-202",
          "archivedAt": null,
          "traits": {
            "type": "Meeting",
            "sentiment": "Positive"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Sample project JSON for testing.
    /// </summary>
    public static string GetSampleProjectJson() => """
    {
      "results": [
        {
          "id": "project-123",
          "name": "Test Project",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "accountId": "account-456",
          "organizationId": "org-789",
          "archivedAt": null,
          "traits": {
            "status": "In Progress",
            "priority": "High"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Sample single account JSON (for Get operations).
    /// </summary>
    public static string GetSampleSingleAccountJson() => """
    {
      "id": "account-123",
      "name": "Test Account",
      "createdAt": "2024-01-01T00:00:00Z",
      "updatedAt": "2024-01-15T00:00:00Z",
      "externalId": "ext-123",
      "organizationId": "org-456",
      "healthScore": 85.5,
      "mrr": 5000,
      "accountOwnerId": "owner-789",
      "lastSeenTimestamp": "2024-01-15T12:00:00Z",
      "status": "active",
      "traits": {
        "industry": "Technology",
        "paymentMethod": "Credit Card"
      }
    }
    """;

    /// <summary>
    /// Empty results JSON for testing empty responses.
    /// </summary>
    public static string GetEmptyResultsJson() => """
    {
      "results": []
    }
    """;
}
