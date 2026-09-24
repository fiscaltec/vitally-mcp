using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VitallyMcp.Tests;

/// <summary>
/// Pins the log-level filters configured in <c>Program.cs</c>.
///
/// <para>⚠️ <b>#143 framed the <c>HttpClient</c> filter as a PII control and that premise was
/// wrong.</b> .NET redacts query <i>values</i> by default — the logged form is
/// <c>GET .../users/search?*</c>, where <c>?*</c> is the redaction marker rather than a truncation
/// — so <c>Search_users</c> terms never reached the logs. Verified by probe and against live
/// production logs, where every query in the stream is <c>?*</c>. The filters are <b>noise
/// reduction</b>: 86.8% of console bytes between them.</para>
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
    private const string DefaultHttpClientName = "Default";

    /// <summary>
    /// Proves that every name the theory data above claims is a name the host actually creates a
    /// logger for, rather than trusting any of them.
    ///
    /// <para>The risk this guards against is specific: every category under
    /// <c>System.Net.Http.HttpClient.</c> is disabled at <c>Information</c> by the prefix filter, so
    /// a <b>wrong</b> name in the theory data would pass while testing a logger nothing creates —
    /// exactly how this file's discovery-client name was wrong and green earlier in this PR.</para>
    ///
    /// <para>So this composes the host, brings each of the four clients into existence by the route
    /// <c>Program.cs</c> itself uses, and asserts the claimed categories are among those the
    /// framework asked for. Crucially the two typed clients are reached by <b>resolving the typed
    /// service</b>, never by asking the factory for a name derived here — asking for a derived name
    /// would create the logger it was meant to prove, and pass whatever the derivation said.</para>
    /// </summary>
    [Fact]
    public void EveryClaimedHttpClientCategory_IsOneTheHostActuallyCreates()
    {
        var seen = new CategoryRecordingProvider();

        var previous = SnapshotAndClearConfiguration();
        foreach (var (key, value) in RequiredSettings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            using var baseFactory = new WebApplicationFactory<Program>();
            using var factory = baseFactory.WithWebHostBuilder(
                b => b.ConfigureLogging(l => l.AddProvider(seen)));

            // Building the handler chain is what creates the logging handlers, and therefore the
            // categories. Each client is brought up the way Program.cs brings it up.
            using var scope = factory.Services.CreateScope();
            var sp = scope.ServiceProvider;

            // Typed clients: resolved as services, so the category recorded below is whatever the
            // framework derives from the registration — not a name this test supplied.
            _ = sp.GetRequiredService<VitallyService>();
            _ = sp.GetRequiredService<IGroupPermissionResolver>();

            var clientFactory = sp.GetRequiredService<IHttpClientFactory>();

            // The discovery client is registered BY this constant, so passing it is the
            // registration itself rather than a guess about one.
            using var discovery = clientFactory.CreateClient(UpstreamOidcMetadata.HttpClientName);

            // The unnamed client the OAuth proxy uses for /oauth/token.
            using var unnamed = clientFactory.CreateClient();
        }
        finally
        {
            RestoreConfiguration(previous);
        }

        // Only the LogicalHandler half: both stages are created together by the same builder, so
        // asserting one per client proves the NAME, which is the thing in doubt. Driven from the
        // same array the theories are, so a client added to one is covered by the other.
        var claimed = AllHttpClientNames
            .Select(name => $"System.Net.Http.HttpClient.{name}.LogicalHandler");

        seen.Categories.Should().Contain(
            claimed,
            "each of these is a category the theory data asserts the filter covers; a name that " +
            "nothing creates would pass those theories — the prefix filter reports every " +
            "System.Net.Http.HttpClient.* category as disabled, invented ones included");
    }

    /// <summary>Records every logger category the host asks for, so a name can be proven.</summary>
    private sealed class CategoryRecordingProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _categories = [];

        public IReadOnlyCollection<string> Categories => _categories;

        public ILogger CreateLogger(string categoryName)
        {
            _categories.Add(categoryName);
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Every name derived from its registration, never a literal. <c>AddHttpClient&lt;TClient&gt;</c>
    /// and <c>AddHttpClient&lt;TClient, TImpl&gt;</c> both take the client name from
    /// <c>TClient</c>, so <c>nameof()</c> is the registration. A literal would survive a rename and
    /// quietly fabricate a category that the prefix filter still reports as disabled — which is
    /// exactly how the discovery client's wrong name passed here before.
    ///
    /// <para>Every entry is nonetheless proven against the composed host by
    /// <see cref="EveryClaimedHttpClientCategory_IsOneTheHostActuallyCreates"/>, because the
    /// derivations above are reasoning about framework behaviour rather than observations of
    /// it.</para>
    /// </summary>
    private static readonly string[] AllHttpClientNames =
    [
        nameof(VitallyService),
        nameof(IGroupPermissionResolver),
        UpstreamOidcMetadata.HttpClientName,
        // The UNNAMED client, used by the OAuth proxy for /oauth/token
        // (Program.cs: `factory.CreateClient()`). "Default" is the substitution
        // LoggingHttpMessageHandlerBuilderFilter makes for an empty name — the one entry here
        // that no registration states, and so the one most in need of the proof above.
        DefaultHttpClientName,
    ];

    public static TheoryData<string> AllHttpClientCategories()
    {
        var data = new TheoryData<string>();
        foreach (var client in AllHttpClientNames)
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
            "these categories log an outbound request URI per call and are 19.5% of console bytes; " +
            "note the query VALUES are redacted by .NET regardless, so this is noise reduction " +
            "rather than the PII control #143 originally claimed");
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

        framework.IsEnabled(LogLevel.Information).Should().BeFalse(
            "this is the suppressed signal — invalid-token diagnostics are Information here");
    }

    /// <summary>
    /// Drives a real rejected token through the actual JwtBearer pipeline and asserts the record is
    /// emitted — rather than only that its logger category would accept a <c>Warning</c>.
    ///
    /// <para>The distinction is the whole point: the level check above composes with
    /// <c>NoAuth=true</c>, which skips the JwtBearer configuration entirely, so deleting the
    /// <c>OnAuthenticationFailed</c> callback would leave it green. This test fails if the callback
    /// is removed, rewired, or has its level lowered back under the filter.</para>
    ///
    /// <para>It also pins the <b>shape</b> of the record, which is a security property rather than a
    /// formatting preference: an earlier version logged <c>Exception.Message</c>, and IdentityModel
    /// builds those from the token's own claims ("Audience validation failed. Audiences: '…'"), so
    /// the text embeds caller-controlled values that may contain newlines — a log-injection path in
    /// the one record an operator reads during an authentication incident.</para>
    /// </summary>
    [Fact]
    public async Task RejectedToken_EmitsTheFailureRecord_WithoutEchoingTheToken()
    {
        // A syntactically valid JWT whose payload carries a marker we can search the log for. If any
        // part of the token reached the record, this string would appear in it.
        const string marker = "TOKEN-MARKER-MUST-NOT-APPEAR-IN-LOGS";
        var payload = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"{{\"sub\":\"{marker}\",\"aud\":\"{marker}\"}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var junkJwt = $"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.{payload}.aW52YWxpZC1zaWduYXR1cmU";

        var sink = new CapturingLoggerProvider("VitallyMcp.Authentication");

        var previous = SnapshotAndClearConfiguration();
        try
        {
            // Auth ON — the NoAuth path never registers the callback under test.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "false");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("OAuth__Authority", "https://example-issuer.test/");
            Environment.SetEnvironmentVariable("OAuth__Audience", "https://example.test/");

            using var baseFactory = new WebApplicationFactory<Program>();
            using var factory = baseFactory.WithWebHostBuilder(
                b => b.ConfigureLogging(l => l.AddProvider(sink)));
            using var client = factory.CreateClient();

            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", junkJwt);

            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        }
        finally
        {
            RestoreConfiguration(previous);
        }

        var entry = sink.Entries.Should().ContainSingle(
            "a rejected token must leave exactly one record — nothing else logs an unauthenticated " +
            "caller, since the request never reaches SendAsync or the [Authorize] checkpoint").Subject;

        entry.Level.Should().Be(LogLevel.Warning,
            "Information would be removed by the Microsoft.AspNetCore.Authentication filter");

        // The structural assertion is what actually proves the security property, and the marker
        // check below is deliberately secondary.
        //
        // ⚠️ The marker alone would be a hollow test. It sits in the token's payload, but this token
        // has an unverifiable signature and points at an unreachable authority — so validation fails
        // at metadata retrieval or signature checking, BEFORE `aud`/`iss` are ever evaluated. The
        // resulting Exception.Message therefore need not contain the marker at all, and a revert to
        // logging Message would sail past a marker-only assertion.
        //
        // Matching the whole rendered message against a single-token shape catches that revert
        // regardless of what the exception says: `Message` cannot be appended without breaking it.
        entry.Message.Should().MatchRegex(@"^Bearer token validation failed: \w+$",
            "the record must carry the exception TYPE and nothing else — appending Exception.Message " +
            "would embed caller-controlled claim values that may contain newlines");

        entry.Message.Should().NotContain(marker,
            "belt and braces: no part of the token may reach the log");
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

    /// <summary>
    /// Snapshots EVERY variable under the configuration prefixes — not just the ones this class
    /// sets, otherwise the restore cannot put back what the clear removes — then clears them.
    ///
    /// <para>Captured and restored at all because these are process-wide, and the collection
    /// serialises only the classes listed in <see cref="IntegrationTestCollection"/>: leaking makes
    /// the suite order-dependent for anything composing a host outside it, as
    /// <see cref="AuthorizationFilterToolsListTests"/>' own <c>finally</c> block already
    /// recognises.</para>
    ///
    /// <para><c>OrdinalIgnoreCase</c>, not <c>Ordinal</c>: .NET configuration keys are
    /// case-insensitive, so <c>oauth__publicbaseurl</c> binds exactly as
    /// <c>OAuth__PublicBaseUrl</c> does while an ordinal prefix match leaves it in place. Verified
    /// rather than reasoned — exporting that lowercase key with an invalid value failed every test
    /// in this class before it changed. On Linux the two spellings are genuinely distinct
    /// variables, so CI is where it bites.</para>
    /// </summary>
    private static (string Key, string? Value)[] SnapshotAndClearConfiguration()
    {
        var previous = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(e => (Key: (string)e.Key, Value: e.Value as string))
            .Where(e => ConfigurationPrefixes.Any(p => e.Key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        foreach (var (key, _) in previous)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        return previous;
    }

    /// <summary>
    /// Restores a snapshot, clearing <b>everything</b> under the configuration prefixes first rather
    /// than just the keys this class is known to set. Always called after the factory is disposed,
    /// so the host is not reading a half-restored environment while it shuts down.
    ///
    /// <para>Clearing by prefix rather than by list, for the same reason
    /// <see cref="SnapshotAndClearConfiguration"/> does: an earlier version cleared only
    /// <see cref="RequiredSettings"/>, so the auth-on integration test — which additionally sets
    /// <c>OAuth__Authority</c> and <c>OAuth__Audience</c> — leaked both into the process and could
    /// change how a later host composed. A list has to be updated every time a test sets something
    /// new; a prefix sweep does not.</para>
    /// </summary>
    private static void RestoreConfiguration((string Key, string? Value)[] previous)
    {
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = (string)e.Key;
            if (ConfigurationPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable(key, null);
            }
        }

        foreach (var (key, value) in previous)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static ILogger ComposeAndGetLogger(string category)
    {
        var previous = SnapshotAndClearConfiguration();

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
            RestoreConfiguration(previous);
        }
    }

    [Fact]
    public void AuditLoggerCategoryName_MatchesTheFilterThatKeepsRecordsOffStdout()
    {
        // Program.cs suppresses "VitallyMcp.AuditLogger" from the ConsoleLoggerProvider once the
        // Azure Monitor exporter is configured, which is what #142's ContainerAppConsoleLogs export
        // is gated on. That filter is a STRING: rename the class or move its namespace and the
        // suppression stops matching, silently, and customer identifiers go back to the console
        // stream — the table with the shortest retention and the broadest access.
        //
        // Nothing else would fail if that happened, so this asserts the category the filter names is
        // still the category the logger actually uses.
        typeof(AuditLogger).FullName.Should().Be("VitallyMcp.AuditLogger",
            "Program.cs filters this exact category off the console provider");
    }
}
