using FluentAssertions;
using Moq.Protected;
using System.Net;
using System.Text.Json;

namespace VitallyMcp.Tests;

/// <summary>
/// Tests for UpdateCheckService. The service calls the public GitHub Releases API and
/// shapes the response for MCP consumption — these tests cover the shaping, version
/// comparison, and asset selection logic against a mocked GitHub response.
/// </summary>
public class UpdateCheckServiceTests
{
    private static string SampleReleaseJson(string tagName = "v99.0.0") => $$"""
    {
      "tag_name": "{{tagName}}",
      "name": "Release {{tagName}}",
      "html_url": "https://github.com/fiscaltec/vitally-mcp/releases/tag/{{tagName}}",
      "published_at": "2026-05-12T10:00:00Z",
      "assets": [
        {
          "name": "VitallyMcp-{{tagName}}-win-x64.mcpb",
          "browser_download_url": "https://example.test/win-x64.mcpb"
        },
        {
          "name": "VitallyMcp-{{tagName}}-win-x64.exe",
          "browser_download_url": "https://example.test/win-x64.exe"
        },
        {
          "name": "VitallyMcp-{{tagName}}-win-arm64.mcpb",
          "browser_download_url": "https://example.test/win-arm64.mcpb"
        },
        {
          "name": "VitallyMcp-{{tagName}}-win-arm64.exe",
          "browser_download_url": "https://example.test/win-arm64.exe"
        }
      ]
    }
    """;

    [Fact]
    public async Task CheckForUpdatesAsync_WhenLatestIsNewer_ShouldReportNotUpToDate()
    {
        // Arrange - mock a release tagged way higher than the running assembly
        var client = TestHelpers.CreateMockHttpClient(SampleReleaseJson("v99.0.0"));
        var service = new UpdateCheckService(client);

        // Act
        var result = await service.CheckForUpdatesAsync();
        using var doc = JsonDocument.Parse(result);

        // Assert
        doc.RootElement.GetProperty("isUpToDate").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("latestVersion").GetString().Should().Be("99.0.0");
        doc.RootElement.GetProperty("releaseUrl").GetString()
            .Should().Be("https://github.com/fiscaltec/vitally-mcp/releases/tag/v99.0.0");
        doc.RootElement.GetProperty("mcpbDownloadUrl").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("exeDownloadUrl").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("publishedAt").GetString().Should().Be("2026-05-12T10:00:00Z");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenLatestIsOlder_ShouldReportUpToDate()
    {
        // Arrange - mock an ancient release; running assembly is whatever the build produced
        var client = TestHelpers.CreateMockHttpClient(SampleReleaseJson("v0.0.1"));
        var service = new UpdateCheckService(client);

        // Act
        var result = await service.CheckForUpdatesAsync();
        using var doc = JsonDocument.Parse(result);

        // Assert
        doc.RootElement.GetProperty("isUpToDate").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("latestVersion").GetString().Should().Be("0.0.1");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldStripVPrefixFromTagName()
    {
        var client = TestHelpers.CreateMockHttpClient(SampleReleaseJson("v3.2.1"));
        var service = new UpdateCheckService(client);

        var result = await service.CheckForUpdatesAsync();
        using var doc = JsonDocument.Parse(result);

        doc.RootElement.GetProperty("latestVersion").GetString().Should().Be("3.2.1");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ShouldSelectAssetMatchingCurrentArchitecture()
    {
        var client = TestHelpers.CreateMockHttpClient(SampleReleaseJson("v3.0.0"));
        var service = new UpdateCheckService(client);

        var result = await service.CheckForUpdatesAsync();
        using var doc = JsonDocument.Parse(result);

        // The architecture suffix should appear in both download URLs
        var arch = doc.RootElement.GetProperty("architecture").GetString();
        arch.Should().BeOneOf("win-x64", "win-arm64");

        var mcpbUrl = doc.RootElement.GetProperty("mcpbDownloadUrl").GetString();
        var exeUrl = doc.RootElement.GetProperty("exeDownloadUrl").GetString();
        mcpbUrl.Should().EndWith($"{arch}.mcpb");
        exeUrl.Should().EndWith($"{arch}.exe");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenAssetForArchitectureMissing_ShouldReturnNullDownloadUrls()
    {
        // Arrange - release with no assets at all
        var emptyReleaseJson = """
        {
          "tag_name": "v99.0.0",
          "html_url": "https://github.com/fiscaltec/vitally-mcp/releases/tag/v99.0.0",
          "published_at": "2026-05-12T10:00:00Z",
          "assets": []
        }
        """;
        var client = TestHelpers.CreateMockHttpClient(emptyReleaseJson);
        var service = new UpdateCheckService(client);

        // Act
        var result = await service.CheckForUpdatesAsync();
        using var doc = JsonDocument.Parse(result);

        // Assert
        doc.RootElement.GetProperty("mcpbDownloadUrl").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("exeDownloadUrl").ValueKind.Should().Be(JsonValueKind.Null);
        // We still get the release URL and latest version
        doc.RootElement.GetProperty("releaseUrl").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("latestVersion").GetString().Should().Be("99.0.0");
    }

    [Fact]
    public async Task CheckForUpdatesAsync_OnGitHubHttpFailure_ShouldReturnErrorPayload()
    {
        // Arrange
        var client = TestHelpers.CreateMockHttpClient("api unreachable", HttpStatusCode.ServiceUnavailable);
        var service = new UpdateCheckService(client);

        // Act
        var result = await service.CheckForUpdatesAsync();
        using var doc = JsonDocument.Parse(result);

        // Assert - graceful failure with a link the user can follow manually
        doc.RootElement.TryGetProperty("error", out var err).Should().BeTrue();
        err.GetString().Should().Contain("Failed to reach GitHub");
        doc.RootElement.GetProperty("releasesUrl").GetString()
            .Should().Be("https://github.com/fiscaltec/vitally-mcp/releases");
        doc.RootElement.GetProperty("currentVersion").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Constructor_ShouldSetUserAgentAndAcceptHeaders()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(SampleReleaseJson());
        _ = new UpdateCheckService(client);

        // Act - trigger a request so we can capture headers
        await client.GetAsync("https://api.github.com/repos/fiscaltec/vitally-mcp/releases/latest");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Moq.Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Headers.UserAgent.Any(p => p.Product != null && p.Product.Name == "VitallyMcp")
                && req.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json")),
            ItExpr.IsAny<CancellationToken>());
    }
}
