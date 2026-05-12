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

    #region Region Tests

    [Fact]
    public void FromEnvironment_WithNoRegion_ShouldDefaultToUs()
    {
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", TestSubdomain);
        Environment.SetEnvironmentVariable("VITALLY_REGION", null);

        try
        {
            var config = VitallyConfig.FromEnvironment();
            config.Region.Should().Be("US");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithRegionEu_ShouldNotRequireSubdomain()
    {
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        Environment.SetEnvironmentVariable("VITALLY_REGION", "EU");

        try
        {
            var config = VitallyConfig.FromEnvironment();
            config.Region.Should().Be("EU");
            config.Subdomain.Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_REGION", null);
        }
    }

    [Theory]
    [InlineData("eu")]
    [InlineData("Eu")]
    [InlineData(" eu ")]
    public void FromEnvironment_WithMixedCaseRegion_ShouldNormaliseToUpper(string regionInput)
    {
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_REGION", regionInput);

        try
        {
            var config = VitallyConfig.FromEnvironment();
            config.Region.Should().Be("EU");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_REGION", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithInvalidRegion_ShouldThrowInvalidOperationException()
    {
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", TestSubdomain);
        Environment.SetEnvironmentVariable("VITALLY_REGION", "APAC");

        try
        {
            var act = () => VitallyConfig.FromEnvironment();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*VITALLY_REGION*US*EU*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
            Environment.SetEnvironmentVariable("VITALLY_REGION", null);
        }
    }

    [Fact]
    public void FromEnvironment_WithRegionUsAndMissingSubdomain_ShouldThrowInvalidOperationException()
    {
        Environment.SetEnvironmentVariable("VITALLY_API_KEY", TestApiKey);
        Environment.SetEnvironmentVariable("VITALLY_SUBDOMAIN", null);
        Environment.SetEnvironmentVariable("VITALLY_REGION", "US");

        try
        {
            var act = () => VitallyConfig.FromEnvironment();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*VITALLY_SUBDOMAIN*US region*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VITALLY_API_KEY", null);
            Environment.SetEnvironmentVariable("VITALLY_REGION", null);
        }
    }

    #endregion
}
