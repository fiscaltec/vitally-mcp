using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VitallyMcp.Tests;

/// <summary>
/// Pins the log-level filters configured in <c>Program.cs</c>.
///
/// <para>The first of these is a <b>PII control, not noise reduction</b>, which is why it is tested
/// rather than left to review. At <c>Information</c> the <c>System.Net.Http.HttpClient.*</c>
/// categories log every outbound request URI <i>including its query string</i>, and
/// <c>Search_users</c> / <c>Search_admins</c> put caller-supplied search terms — potentially names or
/// email addresses — into that query string. That is exactly the data
/// <c>AuditLogger.ResourcePath</c> strips on purpose, escaping through a framework category nobody
/// configured (#143).</para>
///
/// <para>These assertions compose the real host, so they fail if the filters are removed, reordered
/// behind a provider that ignores them, or moved to a config file that does not reach the image —
/// which is the specific trap here: <c>.gitignore</c> and <c>.dockerignore</c> both exclude
/// <c>appsettings.json</c>, so configuring levels there works locally and changes nothing in
/// production.</para>
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class LoggingFilterTests
{
    /// <summary>
    /// The concrete categories the framework uses for a typed client are
    /// <c>System.Net.Http.HttpClient.{Name}.LogicalHandler</c> and <c>.ClientHandler</c>. Asserting
    /// on those rather than the prefix proves the filter actually applies to the categories that
    /// appear in the live log stream, not merely to a name that happens to match.
    /// </summary>
    /// <summary>
    /// The discovery client is named from <see cref="UpstreamOidcMetadata.HttpClientName"/> rather
    /// than a literal, because a literal got this wrong once already: an earlier version asserted on
    /// <c>…HttpClient.upstream.ClientHandler</c>, which is not a registered client at all — the name
    /// is <c>upstream-oidc-discovery</c>, and the truncation came from a grep pattern that stopped at
    /// the hyphen. The prefix filter covers any child category, so the bad name still passed while
    /// testing a logger nothing creates.
    /// </summary>
    private const string DiscoveryClientCategory =
        "System.Net.Http.HttpClient." + UpstreamOidcMetadata.HttpClientName + ".ClientHandler";

    [Theory]
    [InlineData("System.Net.Http.HttpClient.VitallyService.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.VitallyService.ClientHandler")]
    [InlineData("System.Net.Http.HttpClient.IGroupPermissionResolver.LogicalHandler")]
    [InlineData(DiscoveryClientCategory)]
    public void HttpClientCategories_DoNotLogAtInformation_SoQueryStringsStayOutOfLogs(string category)
    {
        var logger = ComposeAndGetLogger(category);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse(
            "Information on this category logs the outbound request URI including its query string, " +
            "and Search_users/Search_admins put caller-supplied search terms there");
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.VitallyService.ClientHandler")]
    [InlineData(DiscoveryClientCategory)]
    public void HttpClientCategories_StillLogWarnings_SoFailuresRemainVisible(string category)
    {
        var logger = ComposeAndGetLogger(category);

        logger.IsEnabled(LogLevel.Warning).Should().BeTrue(
            "the filter is Warning rather than None deliberately — a failing outbound call must " +
            "still surface, and this server has only one LogError call site of its own");
    }

    [Theory]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler")]
    [InlineData("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService")]
    [InlineData("Microsoft.AspNetCore.Routing.EndpointMiddleware")]
    public void FrameworkNoiseCategories_AreQuietAtInformation_ButStillReportFaults(string category)
    {
        var logger = ComposeAndGetLogger(category);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse(
            "these were ~90% of console volume in a live sample and carry no audit or failure signal");

        // Asserted in the same test rather than left implied: a regression from Warning to None
        // would satisfy the line above while silently hiding authentication, authorisation and
        // routing faults — and those framework warnings are most of what reports a fault here,
        // since the application has exactly one LogError call site of its own.
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue(
            "the filters are Warning rather than None so genuine faults still surface");
    }

    /// <summary>
    /// The audit trail must survive the noise filters. <c>AuditLogger</c> writes its action record at
    /// <c>Information</c>, so a broad filter applied to the wrong prefix would silently delete the
    /// thing the whole trail exists for.
    /// </summary>
    [Fact]
    public void AuditLogger_StillLogsAtInformation()
    {
        var logger = ComposeAndGetLogger(typeof(AuditLogger).FullName!);

        logger.IsEnabled(LogLevel.Information).Should().BeTrue(
            "LogAction records every audited call at Information; filtering it away would remove " +
            "the audit trail while looking like noise reduction");
    }

    private static ILogger ComposeAndGetLogger(string category)
    {
        // Process-wide and read at composition time, so set every one this host needs rather than
        // relying on whatever a sibling class left behind.
        Environment.SetEnvironmentVariable("OAuth__NoAuth", "true");
        Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
        Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
        Environment.SetEnvironmentVariable("Vitally__Region", "EU");
        Environment.SetEnvironmentVariable("OAuth__Authority", null);
        Environment.SetEnvironmentVariable("OAuth__Audience", null);

        // Both are disposed: WithWebHostBuilder returns a new factory rather than mutating the
        // receiver, so disposing only the result leaks the constructor's one.
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(_ => { });

        return factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);
    }
}
