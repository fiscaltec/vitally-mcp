using FluentAssertions;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

/// <summary>
/// Tests for CustomTraitsTools to verify correct API endpoint usage and parameter passing.
/// </summary>
public class CustomTraitsToolsTests
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
    public async Task ListCustomTraits_WithAccountsModel_ShouldReturnRawCustomTraits()
    {
        // Arrange - GetRawAsync returns raw body unchanged (bare array, not {results} envelope)
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleCustomTraitsJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomTraitsTools.ListCustomTraits(service, "accounts");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Payment Method");
        result.Should().Contain("paymentMethod");
        result.Should().Contain("STRING");
    }

    [Fact]
    public async Task ListCustomTraits_WithCustomObjectsModel_ShouldRequireCustomObjectId()
    {
        // Arrange - When customObjectId provided it gets added as a query parameter
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleCustomTraitsJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomTraitsTools.ListCustomTraits(service, "customObjects", "cobj-123");

        // Assert
        result.Should().NotBeNullOrEmpty();
        result.Should().Contain("Payment Method");
    }
}
