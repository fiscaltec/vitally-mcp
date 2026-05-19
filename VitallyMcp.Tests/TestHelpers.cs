using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    /// Builds a VitallyService for tests. Uses DevelopmentApiKey path so no Key Vault is required.
    /// Defaults to US region with a test subdomain to preserve historical URL shapes in assertions.
    /// </summary>
    public static VitallyService BuildVitallyService(
        HttpClient httpClient,
        string region = "US",
        string? subdomain = "test-subdomain",
        string apiKey = "sk_live_test_key")
    {
        var options = Options.Create(new VitallyServerOptions
        {
            Region = region,
            Subdomain = subdomain,
            DevelopmentApiKey = apiKey
        });
        var provider = new VitallyApiKeyProvider(
            options,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<VitallyApiKeyProvider>.Instance);
        return new VitallyService(httpClient, options, provider);
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

    /// <summary>
    /// Sample conversation JSON for testing.
    /// </summary>
    public static string GetSampleConversationJson() => """
    {
      "results": [
        {
          "id": "conv-123",
          "externalId": "ext-conv-123",
          "subject": "Test Conversation",
          "authorId": "user-456",
          "accountId": "account-789",
          "organizationId": "org-101",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ],
      "next": "cursor-conv-next"
    }
    """;

    /// <summary>
    /// Sample message JSON for testing.
    /// </summary>
    public static string GetSampleMessageJson() => """
    {
      "results": [
        {
          "id": "msg-123",
          "type": "email",
          "externalId": "ext-msg-123",
          "timestamp": "2024-01-15T12:00:00Z",
          "message": "Hello there",
          "from": "sender@example.com",
          "to": "recipient@example.com"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample NPS response JSON for testing.
    /// </summary>
    public static string GetSampleNpsResponseJson() => """
    {
      "results": [
        {
          "id": "nps-123",
          "externalId": "ext-nps-123",
          "userId": "user-456",
          "score": 9,
          "feedback": "Great product!",
          "respondedAt": "2024-01-15T12:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample project template JSON for testing.
    /// </summary>
    public static string GetSampleProjectTemplateJson() => """
    {
      "results": [
        {
          "id": "tmpl-123",
          "name": "Onboarding Template",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "projectCategoryId": "cat-456",
          "description": "Standard onboarding template",
          "traits": {
            "phase": "Onboarding",
            "complexity": "Medium"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Sample project category JSON for testing.
    /// </summary>
    public static string GetSampleProjectCategoryJson() => """
    {
      "results": [
        {
          "id": "cat-123",
          "name": "Onboarding",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample admin search JSON for testing.
    /// </summary>
    public static string GetSampleAdminJson() => """
    {
      "results": [
        {
          "id": "admin-123",
          "name": "Admin User",
          "email": "admin@example.com"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample note category JSON for testing.
    /// </summary>
    public static string GetSampleNoteCategoryJson() => """
    {
      "results": [
        {
          "id": "ncat-123",
          "name": "Customer Call",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample task category JSON for testing.
    /// </summary>
    public static string GetSampleTaskCategoryJson() => """
    {
      "results": [
        {
          "id": "tcat-123",
          "name": "Follow-up",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample custom object JSON for testing.
    /// </summary>
    public static string GetSampleCustomObjectJson() => """
    {
      "results": [
        {
          "id": "cobj-123",
          "name": "Subscription",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample custom object instance JSON for testing.
    /// </summary>
    public static string GetSampleCustomObjectInstanceJson() => """
    {
      "results": [
        {
          "id": "inst-123",
          "externalId": "ext-inst-123",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample meeting JSON for testing.
    /// </summary>
    public static string GetSampleMeetingJson() => """
    {
      "results": [
        {
          "id": "mtg-123",
          "title": "Quarterly Review",
          "externalId": "ext-mtg-123",
          "startDateTime": "2024-01-15T14:00:00Z",
          "endDateTime": "2024-01-15T15:00:00Z",
          "location": "Zoom",
          "source": "zoom",
          "accountIds": ["account-456"],
          "organizationIds": ["org-789"],
          "participants": [
            {
              "id": "part-1",
              "userId": "user-1",
              "type": "organizer"
            }
          ],
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "traits": {
            "topic": "QBR",
            "outcome": "Positive"
          }
        }
      ],
      "next": "cursor-mtg-next"
    }
    """;

    /// <summary>
    /// Sample meeting transcript JSON for testing.
    /// </summary>
    public static string GetSampleMeetingTranscriptJson() => """
    {
      "results": [
        {
          "id": "trans-123",
          "meetingId": "mtg-456",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// Sample raw meeting transcript JSON for testing (non-results envelope).
    /// </summary>
    public static string GetSampleRawTranscriptJson() => """
    {
      "id": "trans-123",
      "meetingId": "mtg-456",
      "transcript": [
        {
          "speaker": {
            "externalId": "speaker-1",
            "name": "Alice"
          },
          "sentences": [
            { "text": "Hello everyone", "startTime": 0.0, "endTime": 1.5 }
          ]
        }
      ]
    }
    """;

    /// <summary>
    /// Sample custom traits (custom fields) raw JSON for testing.
    /// </summary>
    public static string GetSampleCustomTraitsJson() => """
    [
      {
        "label": "Payment Method",
        "type": "STRING",
        "path": "paymentMethod",
        "createdAt": "2024-01-01T00:00:00Z"
      },
      {
        "label": "Employees",
        "type": "NUMBER",
        "path": "employees",
        "createdAt": "2024-01-01T00:00:00Z"
      }
    ]
    """;

    /// <summary>
    /// Sample survey responses raw JSON for testing (uses 'data' envelope).
    /// </summary>
    public static string GetSampleSurveyResponsesJson() => """
    {
      "data": [
        {
          "id": "sres-123",
          "surveyId": "survey-456",
          "userId": "user-789",
          "submittedAt": "2024-01-15T12:00:00Z",
          "questionResponses": [
            { "questionId": "q-1", "answer": "Very satisfied" }
          ]
        }
      ],
      "next": "cursor-survey-next"
    }
    """;

    /// <summary>
    /// Sample single survey response raw JSON for testing.
    /// </summary>
    public static string GetSampleSingleSurveyResponseJson() => """
    {
      "id": "sres-123",
      "surveyId": "survey-456",
      "userId": "user-789",
      "submittedAt": "2024-01-15T12:00:00Z",
      "questionResponses": [
        { "questionId": "q-1", "answer": "Very satisfied" }
      ]
    }
    """;

    /// <summary>
    /// Sample survey question raw JSON for testing.
    /// </summary>
    public static string GetSampleSurveyQuestionJson() => """
    {
      "id": "q-123",
      "surveyId": "survey-456",
      "label": "How satisfied are you?",
      "type": "rating"
    }
    """;
}
