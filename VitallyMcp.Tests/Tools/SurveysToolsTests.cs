using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for SurveysTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class SurveysToolsTests
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
    public async Task ListSurveyResponses_WithValidSurveyId_ShouldReturnRawDataEnvelope()
    {
        // Arrange - GetRawAsync returns the raw body unchanged (uses 'data' envelope, not 'results')
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSurveyResponsesJson());
        var service = CreateService(mockClient);

        // Act
        var result = await SurveysTools.ListSurveyResponses(service, "survey-456");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("\"data\"");
        result.Should().Contain("sres-123");
        result.Should().Contain("questionResponses");
    }

    [Fact]
    public async Task GetSurveyResponse_WithValidResponseId_ShouldReturnRawResponse()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSingleSurveyResponseJson());
        var service = CreateService(mockClient);

        // Act
        var result = await SurveysTools.GetSurveyResponse(service, "sres-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("sres-123");
        result.Should().Contain("Very satisfied");
    }

    [Fact]
    public async Task GetSurveyQuestion_WithValidQuestionId_ShouldReturnRawQuestion()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleSurveyQuestionJson());
        var service = CreateService(mockClient);

        // Act
        var result = await SurveysTools.GetSurveyQuestion(service, "q-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("q-123");
        result.Should().Contain("How satisfied are you?");
    }
}
