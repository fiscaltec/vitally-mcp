using FluentAssertions;

namespace VitallyMcp.Tests;

/// <summary>
/// Tests for VitallyConfig environment variable loading and validation.
/// These tests run sequentially to avoid environment variable conflicts.
/// </summary>
[Collection("VitallyConfig Tests")]
public class VitallyConfigTests
{
    private const string TestApiKey = "sk_live_test_key_12345";
    private const string TestSubdomain = "test-subdomain";

    [Fact]
    public void FromEnvironment_WithValidEnvironmentVariables_ShouldSucceed()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", TestSubdomain);

        try
        {
            // Act
            var config = VitallyConfig.FromEnvironment();

            // Assert
            config.ApiKey.Should().Be(TestApiKey);
            config.Subdomain.Should().Be(TestSubdomain);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithMissingApiKey_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", TestSubdomain);

        try
        {
            // Act
            var act = () => VitallyConfig.FromEnvironment();

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*VITALLY_API_KEY*not set*");
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithEmptyApiKey_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", "");
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", TestSubdomain);

        try
        {
            // Act
            var act = () => VitallyConfig.FromEnvironment();

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*VITALLY_API_KEY*not set*");
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithMissingSubdomain_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);

        try
        {
            // Act
            var act = () => VitallyConfig.FromEnvironment();

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*VITALLY_SUBDOMAIN*not set*");
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithEmptySubdomain_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", "");

        try
        {
            // Act
            var act = () => VitallyConfig.FromEnvironment();

            // Assert
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*VITALLY_SUBDOMAIN*not set*");
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithBothMissing_ShouldThrowInvalidOperationException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);

        try
        {
            // Act
            var act = () => VitallyConfig.FromEnvironment();

            // Assert
            act.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Theory]
    [InlineData("sk_live_key")]
    [InlineData("sk_live_another_key_123")]
    [InlineData("sk_live_ABCDEF1234567890")]
    public void FromEnvironment_WithVariousValidApiKeys_ShouldSucceed(string apiKey)
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", TestSubdomain);

        try
        {
            // Act
            var config = VitallyConfig.FromEnvironment();

            // Assert
            config.ApiKey.Should().Be(apiKey);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Theory]
    [InlineData("my-subdomain")]
    [InlineData("fiscaltec")]
    [InlineData("company123")]
    public void FromEnvironment_WithVariousValidSubdomains_ShouldSucceed(string subdomain)
    {
        // Arrange
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", subdomain);

        try
        {
            // Act
            var config = VitallyConfig.FromEnvironment();

            // Assert
            config.Subdomain.Should().Be(subdomain);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }
}
