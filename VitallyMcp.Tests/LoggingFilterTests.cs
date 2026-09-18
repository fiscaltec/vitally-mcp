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
    /// <summary>
    /// <b>Every</b> typed-client category, both handler stages, driven from one source so the two
    /// theories below cannot drift apart. The filter is a single prefix today, so any one entry
    /// would prove it works — the completeness is insurance against a future per-category override
    /// re-enabling `Information` (or dropping to `None`) for one client while the suite stays green.
    /// </summary>
    public static TheoryData<string> AllHttpClientCategories()
    {
        // Every name derived from its registration, never a literal. `AddHttpClient<TClient>` and
        // `AddHttpClient<TClient, TImpl>` both take the client name from TClient, so nameof() is the
        // registration. A literal would survive a rename and quietly fabricate a category that the
        // prefix filter still reports as disabled — which is exactly how the discovery client's
        // wrong name passed here before.
        var data = new TheoryData<string>();
        foreach (var client in new[]
                 {
                     nameof(VitallyService),
                     nameof(IGroupPermissionResolver),
                     UpstreamOidcMetadata.HttpClientName,
                 })
        {
            foreach (var stage in new[] { "LogicalHandler", "ClientHandler" })
            {
                data.Add($"System.Net.Http.HttpClient.{client}.{stage}");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllHttpClientCategories))]
    public void HttpClientCategories_DoNotLogAtInformation_SoQueryStringsStayOutOfLogs(string category)
    {
        var logger = ComposeAndGetLogger(category);

        logger.IsEnabled(LogLevel.Information).Should().BeFalse(
            "Information on this category logs the outbound request URI including its query string, " +
            "and Search_users/Search_admins put caller-supplied search terms there");
    }

    [Theory]
    [MemberData(nameof(AllHttpClientCategories))]
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

    /// <summary>
    /// The variables this host needs, each read by <c>Program.cs</c> at composition time — before
    /// <c>WebApplicationFactory</c> can inject configuration — so environment variables are the only
    /// override that works.
    ///
    /// <para>The nulls matter as much as the values. Each one names an input that can fail host
    /// composition <b>before</b> any assertion here runs, and the resulting error points at OAuth or
    /// Key Vault rather than at logging:</para>
    /// <list type="bullet">
    ///   <item><c>Vitally__KeyVaultUri</c> — <c>StartupGuards.EnsureSafeAuthConfig</c> refuses
    ///     <c>NoAuth=true</c> alongside a Key Vault URI, and this fixture sets <c>NoAuth</c>.</item>
    ///   <item><c>OAuth__SharedClientId</c> — <c>NoAuth</c> does <b>not</b> disable the proxy;
    ///     <c>Program</c> derives <c>proxyEnabled</c> from this id and <c>Validate()</c> then demands
    ///     the <c>Authority</c> cleared below.</item>
    ///   <item><c>OAuth__PublicBaseUrl</c> — <c>Validate()</c> rejects a non-https value before any
    ///     proxy-related early return. <b>This one is not hypothetical:</b>
    ///     <see cref="ResourceMetadataDiscoveryTests"/> sets it (and <c>OAuth__Resource</c>) inside
    ///     its <c>CreateHost</c> and never restores them, so this fixture really does inherit them.
    ///     Today's leaked value is valid https and therefore harmless — which is luck, not
    ///     design.</item>
    /// </list>
    /// </summary>
    private static readonly (string Key, string? Value)[] HostEnvironment =
    [
        ("OAuth__NoAuth", "true"),
        ("Authorization__ReadOnly", "false"),
        ("Vitally__DevelopmentApiKey", "sk_test_dummy"),
        ("Vitally__Region", "EU"),
        ("Vitally__KeyVaultUri", null),
        ("OAuth__Authority", null),
        ("OAuth__Audience", null),
        ("OAuth__Resource", null),
        ("OAuth__PublicBaseUrl", null),
        ("OAuth__SharedClientId", null),
    ];

    private static ILogger ComposeAndGetLogger(string category)
    {
        // Captured and restored, matching AuthorizationFilterToolsListTests — these are process-wide,
        // and the collection serialises only the classes listed in IntegrationTestCollection, so
        // leaking them makes the suite order-dependent for anything composing a host outside it.
        var previous = HostEnvironment
            .Select(e => (e.Key, Value: Environment.GetEnvironmentVariable(e.Key)))
            .ToArray();

        foreach (var (key, value) in HostEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            // Both are disposed: WithWebHostBuilder returns a new factory rather than mutating the
            // receiver, so disposing only the result leaks the constructor's one.
            using var baseFactory = new WebApplicationFactory<Program>();
            using var factory = baseFactory.WithWebHostBuilder(_ => { });

            return factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category);
        }
        finally
        {
            // After the factory is disposed, so the host is not reading a half-restored environment
            // while it shuts down.
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
