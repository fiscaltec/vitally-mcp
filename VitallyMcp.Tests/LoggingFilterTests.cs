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
    /// <b>Every</b> typed-client category, both handler stages, driven from one source so the two
    /// theories below cannot drift apart.
    ///
    /// <para>The concrete categories the framework uses are
    /// <c>System.Net.Http.HttpClient.{Name}.LogicalHandler</c> and <c>.ClientHandler</c>. Asserting
    /// on those rather than on the prefix proves the filter applies to the categories that actually
    /// appear in the live log stream, not merely to a name that happens to match it.</para>
    ///
    /// <para>The filter is a single prefix today, so any one entry would prove it works — the
    /// completeness is insurance against a future per-category override re-enabling
    /// <c>Information</c>, or dropping to <c>None</c>, for one client while the suite stays
    /// green.</para>
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
            "these are 67.3% of console bytes (measured 2026-09-18) and are filtered for live-stream " +
            "readability — not for cost, which is single-figure pounds a year either way");

        // Asserted in the same test rather than left implied: a regression from Warning to None
        // would satisfy the line above while silently hiding faults — and framework warnings are
        // most of what reports one here, since the application has exactly one LogError call site.
        logger.IsEnabled(LogLevel.Warning).Should().BeTrue(
            "the filters are Warning rather than None so genuine faults still surface");
    }

    /// <summary>
    /// The one signal these filters genuinely suppress, and the record that replaces it.
    ///
    /// <para><c>Microsoft.AspNetCore.Authorization</c> logs its <i>failures</i> at
    /// <c>Information</c> — <c>"Authorization failed. These requirements were not met:
    /// DenyAnonymousAuthorizationRequirement"</c> — so unlike the other three, filtering it to
    /// <c>Warning</c> removes a real failure signal rather than success chatter. An earlier comment
    /// in <c>Program.cs</c> claimed otherwise and was wrong.</para>
    ///
    /// <para>That is acceptable only because the case that matters is recorded better elsewhere: an
    /// <b>authenticated</b> caller denied a tool produces <c>AuditLogger.LogToolCallDenied</c> at
    /// <c>Warning</c>, carrying the object id, tool name and required permission. This test pins
    /// that the replacement survives the filters — without it, the trade is unverified and the
    /// suppression indefensible. What is genuinely given up is <b>anonymous</b> 401 probes, which
    /// are the normal MCP handshake.</para>
    /// </summary>
    [Fact]
    public void AuditDenialRecords_SurviveTheFilters_ReplacingTheSuppressedFrameworkSignal()
    {
        var framework = ComposeAndGetLogger("Microsoft.AspNetCore.Authorization.DefaultAuthorizationService");
        var audit = ComposeAndGetLogger(typeof(AuditLogger).FullName!);

        framework.IsEnabled(LogLevel.Information).Should().BeFalse(
            "this is the suppressed signal — stated explicitly so the trade is visible, not implied");

        audit.IsEnabled(LogLevel.Warning).Should().BeTrue(
            "LogDenied and LogToolCallDenied are the replacement and must outlive the filters; if " +
            "this ever fails, suppressing the framework's authorisation failures stops being defensible");
    }

    /// <summary>
    /// The second suppressed signal, and its replacement.
    ///
    /// <para><c>JwtBearerHandler</c> reports a failed token at <c>Information</c> ("Bearer was not
    /// authenticated. Failure message: …"), which the
    /// <c>Microsoft.AspNetCore.Authentication</c> filter removes. This case is worse than the
    /// authorisation one: an unauthenticated caller never reaches <c>VitallyService.SendAsync</c> or
    /// the SDK's <c>[Authorize]</c> checkpoint, so <b>no</b> <c>AuditLogger</c> record would fire
    /// either. A signing-key rotation, clock skew or a run of forged tokens would all look exactly
    /// like silence.</para>
    ///
    /// <para><c>Program.cs</c> therefore logs its own <c>OnAuthenticationFailed</c> event at
    /// <c>Warning</c> under <c>VitallyMcp.Authentication</c>. This pins that the replacement
    /// <i>survives the filters</i> — which is the concern the filters create. It does not assert the
    /// event fires; that is framework behaviour, and proving it needs a real JWT path rather than
    /// the test auth scheme the sibling suites use.</para>
    /// </summary>
    [Fact]
    public void AuthenticationFailureRecord_SurvivesTheFilters()
    {
        var framework = ComposeAndGetLogger("Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler");
        var ours = ComposeAndGetLogger("VitallyMcp.Authentication");

        framework.IsEnabled(LogLevel.Information).Should().BeFalse(
            "this is the suppressed signal — invalid-token diagnostics are Information here");

        ours.IsEnabled(LogLevel.Warning).Should().BeTrue(
            "OnAuthenticationFailed is the only record of a rejected token once the framework's " +
            "Information diagnostic is filtered; nothing else fires for an unauthenticated caller");
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
    /// <para>Only these four are <i>set</i>. Everything else under the configuration prefixes in
    /// <see cref="ConfigurationPrefixes"/>
    /// is <b>cleared wholesale</b> by <see cref="ComposeAndGetLogger"/> rather than enumerated,
    /// which is deliberate: naming individual keys to clear is a losing game. Three review rounds
    /// each found another one that fails host composition before a single assertion runs —
    /// <c>Vitally__KeyVaultUri</c> (<c>StartupGuards.EnsureSafeAuthConfig</c> refuses it alongside
    /// <c>NoAuth</c>), <c>OAuth__SharedClientId</c> and <c>OAuth__PublicBaseUrl</c>, then
    /// <c>OAuth__SharedClientSecret</c>, <c>OAuth__UpstreamResourceScope</c> and
    /// <c>Authorization__LiveGroupCheck</c> with its group ids. Each failure surfaces as an OAuth or
    /// Key Vault error with nothing to do with logging.</para>
    ///
    /// <para>The leak is real rather than theoretical: <see cref="ResourceMetadataDiscoveryTests"/>
    /// sets <c>OAuth__PublicBaseUrl</c> and <c>OAuth__Resource</c> inside its <c>CreateHost</c> and
    /// never restores them, so this fixture inherits them from a sibling in the same collection.
    /// Clearing by prefix is immune to the next such addition, which an enumeration is not.</para>
    /// </summary>
    private static readonly (string Key, string Value)[] RequiredSettings =
    [
        ("OAuth__NoAuth", "true"),
        ("Authorization__ReadOnly", "false"),
        ("Vitally__DevelopmentApiKey", "sk_test_dummy"),
        ("Vitally__Region", "EU"),
    ];

    /// <summary>
    /// Every configuration prefix <c>Program.cs</c> binds from the environment. Anything under these
    /// is cleared before composing, so the host sees exactly <see cref="RequiredSettings"/>.
    /// </summary>
    private static readonly string[] ConfigurationPrefixes =
        ["OAuth__", "Authorization__", "Vitally__", "Audit__", "ToolsListCache__", "Logging__"];

    // Logging__ is in that list for a reason specific to this class: WebApplication.CreateBuilder
    // reads it, so an ambient Logging__LogLevel__Default=Warning makes
    // AuditLogger_StillLogsAtInformation fail even when Program.cs's filters are exactly right.
    // Verified: exporting that variable failed 1 of 17 before it was cleared here.

    private static ILogger ComposeAndGetLogger(string category)
    {
        // Snapshot EVERY variable under the configuration prefixes, not just the ones we set —
        // otherwise the restore below cannot put back what the clear is about to remove. Captured
        // and restored at all because these are process-wide, and the collection serialises only
        // the classes listed in IntegrationTestCollection: leaking makes the suite order-dependent
        // for anything composing a host outside it, as AuthorizationFilterToolsListTests' own
        // finally block already recognises.
        var previous = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Key: (string)e.Key, Value: e.Value as string))
            // OrdinalIgnoreCase, not Ordinal: .NET configuration keys are case-insensitive, so
            // `oauth__publicbaseurl` binds exactly as `OAuth__PublicBaseUrl` does — while an
            // Ordinal prefix match leaves it in place. Verified rather than reasoned: exporting
            // that lowercase key with an invalid value failed all 17 tests here before this
            // changed. On Linux the two are genuinely distinct variables, so CI is where it bites.
            .Where(e => ConfigurationPrefixes.Any(p => e.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var (key, _) in previous)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        foreach (var (key, value) in RequiredSettings)
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
            // while it shuts down. The four we set are cleared first: they are not necessarily in
            // the snapshot (if they were unset before), and leaving them behind is the leak this
            // whole block exists to prevent.
            foreach (var (key, _) in RequiredSettings)
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}
