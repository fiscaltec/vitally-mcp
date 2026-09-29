using System.Net;
using Azure;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace VitallyMcp.Tests;

/// <summary>
/// Failure logging (#94, phase 5). Before this, an upstream failure, a validation failure and a Key
/// Vault failure each reached the client and left no server-side trace at all.
/// </summary>
/// <remarks>
/// Every test here also asserts what the record must <b>not</b> carry. A failure record that quoted the
/// upstream body or an exception message would move customer data into the least-restricted telemetry
/// stream this server has, which is presumably why the failures were left unlogged in the first place.
/// </remarks>
public class FailureLoggingTests
{
    private const string CustomerEmail = "alice@example.com";

    [Fact]
    public async Task SendAsync_LogsAnErrorForANon2xx_WithStatusAndPath_ButNeverTheBodyOrQuery()
    {
        using var client = TestHelpers.CreateMockHttpClient(
            "{\"message\":\"no user " + CustomerEmail + "\"}", HttpStatusCode.InternalServerError);
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        // A sentinel without reserved characters, so URL-encoding cannot disguise a leak and let the
        // assertion below pass for the wrong reason.
        const string searchTerm = "searchtermsentinel";
        var act = () => service.GetRawAsync("users/search",
            new Dictionary<string, string> { ["query"] = searchTerm });
        await act.Should().ThrowAsync<HttpRequestException>();

        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject.Message;
        entry.Should().Contain("500").And.Contain("GET").And.Contain("/resources/users");
        entry.Should().NotContain(CustomerEmail, "the upstream body may be customer data");
        entry.Should().NotContain(searchTerm, "the query string carries caller search terms");
        logger.Exceptions.Should().AllSatisfy(e => e.Should().BeNull(
            "nothing is attached — an exception's ToString() would carry the body the template omits"));
    }

    [Fact]
    public async Task SendAsync_NeverLogsAPathSegmentTheCallerTyped()
    {
        // Tools put caller strings into the path unescaped (`accounts/{accountId}/users`), and a 404
        // is exactly the case where that segment is NOT a Vitally id but whatever the model typed —
        // a customer name, an email. This category stays on the console, unlike AuditLogger's, so
        // only the resource type is logged; the correlation id leads to the full path in AppEvents.
        const string typedId = "acmeholdingsltd";
        using var client = TestHelpers.CreateMockHttpClient("{}", HttpStatusCode.NotFound);
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        var act = () => service.GetRawAsync($"accounts/{typedId}/users");
        await act.Should().ThrowAsync<HttpRequestException>();

        var entry = logger.Entries.Should().ContainSingle().Subject.Message;
        entry.Should().Contain("/resources/accounts/…");
        entry.Should().NotContain(typedId);
    }

    [Theory]
    [InlineData("../acmeholdingsltd")]
    [InlineData("%2E%2E/acmeholdingsltd")]
    [InlineData("..\\acmeholdingsltd")]
    [InlineData("./../acmeholdingsltd/x")]
    public async Task SendAsync_CannotBeSteeredIntoLoggingCallerText_ByDotSegments(string typedId)
    {
        // Uri normalises `../` BEFORE the path is read, so "the first segment after /resources/" can
        // be made into the caller's own text — a customer name on the console. The resource type is
        // therefore checked against the set this code builds, not taken from a parsed path, and
        // anything else reads `unrecognised`: fail closed, as KnownToolNames does for tool names.
        using var client = TestHelpers.CreateMockHttpClient("{}", HttpStatusCode.NotFound);
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        var act = () => service.GetResourceByIdAsync("organizations", typedId);
        await act.Should().ThrowAsync<HttpRequestException>();

        logger.Entries.Should().ContainSingle().Subject.Message.Should().NotContain("acmeholdingsltd");
    }

    [Fact]
    public async Task SendAsync_LogsAnUnrecognisedResourceType_AsUnrecognised()
    {
        using var client = TestHelpers.CreateMockHttpClient("{}", HttpStatusCode.NotFound);
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        var act = () => service.GetRawAsync("acmeholdingsltd/x");
        await act.Should().ThrowAsync<HttpRequestException>();

        var entry = logger.Entries.Should().ContainSingle().Subject.Message;
        entry.Should().Contain("unrecognised").And.NotContain("acmeholdingsltd");
    }

    [Fact]
    public void ToolCallFailureLog_DoesNotPrintAnUnknownRequestError_WhenThereWasAResponse()
    {
        // HttpRequestError is Unknown whenever a response arrived, which reads as "an unknown error".
        var logger = new CapturingLogger<object>();

        ToolCallFailureLog.Write(logger, "List_users",
            new HttpRequestException("x", null, HttpStatusCode.InternalServerError), "corr", CancellationToken.None);

        logger.Entries.Single().Message.Should().Contain("error=none").And.NotContain("Unknown");
    }

    [Fact]
    public void ToolCallFailureLog_TreatsAnArgumentExceptionSubtype_AsOurFault()
    {
        // The plain ArgumentException is what this code throws for bad input, and what the SDK throws
        // for a missing parameter. An ArgumentOutOfRangeException or ArgumentNullException is almost
        // always a bug of ours — a slice, a null — and hiding it as "rejected its arguments" at
        // Warning would bury a server fault as the caller's mistake.
        var logger = new CapturingLogger<object>();

        ToolCallFailureLog.Write(logger, "List_users", new ArgumentOutOfRangeException("start"), "corr", CancellationToken.None);
        ToolCallFailureLog.Write(logger, "List_users", new ArgumentNullException("value"), "corr", CancellationToken.None);

        logger.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Error);
    }

    public static TheoryData<int, LogLevel> StatusLevels => new()
    {
        // The caller's mistake: a wrong id, a missing field. Worth seeing, not worth paging for.
        { 400, LogLevel.Warning }, { 404, LogLevel.Warning }, { 409, LogLevel.Warning },
        { 422, LogLevel.Warning },
        // Ours or Vitally's: a bad or revoked shared key, the rate-limit budget, an outage — and
        // 407/408, which are infrastructure conditions despite sitting in the 4xx range.
        { 401, LogLevel.Error }, { 403, LogLevel.Error }, { 407, LogLevel.Error },
        { 408, LogLevel.Error }, { 429, LogLevel.Error },
        { 500, LogLevel.Error }, { 503, LogLevel.Error },
        // An unfollowed redirect or a non-standard code is a misconfiguration, never the caller.
        { 302, LogLevel.Error }, { 600, LogLevel.Error },
    };

    [Theory]
    [MemberData(nameof(StatusLevels))]
    public async Task SendAsync_ChoosesTheLevelByStatus(int status, LogLevel expected)
    {
        using var client = TestHelpers.CreateMockHttpClient("{}", (HttpStatusCode)status);
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        var act = () => service.GetResourcesAsync("organizations");
        await act.Should().ThrowAsync<HttpRequestException>();

        logger.Entries.Should().ContainSingle().Subject.Level.Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(StatusLevels))]
    public void ToolCallFailureLog_ChoosesTheLevelByStatus(int status, LogLevel expected)
    {
        var logger = new CapturingLogger<object>();

        ToolCallFailureLog.Write(logger, "List_users",
            new HttpRequestException("x", null, (HttpStatusCode)status), "corr", CancellationToken.None);

        logger.Entries.Should().ContainSingle().Subject.Level.Should().Be(expected);
    }

    [Fact]
    public async Task OrganizationSummary_LogsASectionItCouldNotResolve_WithoutTheCallersObjectName()
    {
        // A renamed custom object in Vitally makes every summary return an error section forever,
        // and the tool still succeeds — so without this the drift is invisible server-side. The
        // object name is caller-overridable, so the fixed SECTION label is logged, never the name.
        // Sequenced, one response per upstream call: SendAsync disposes each response, so a single
        // shared one fails the second call — and the section would then read "catalogue unavailable",
        // which is how this test first passed for the wrong reason.
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(
            "{\"id\":\"org-1\",\"name\":\"Acme\"}",
            "{\"results\":[{\"id\":\"co-1\",\"name\":\"somethingElse\"}]}");
        using var _client = client;
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        await service.GetOrganizationSummaryAsync("org-1", null, "callerGoalsName", "callerFeedbackName");

        logger.Entries.Should().HaveCount(2).And.OnlyContain(e => e.Level == LogLevel.Warning);
        logger.Entries.Should().OnlyContain(e => e.Message.Contains("not found in the catalogue", StringComparison.Ordinal),
            "the reason is what sends the operator to the right place");
        logger.Entries.Should().Contain(e => e.Message.Contains("goals", StringComparison.Ordinal));
        logger.Entries.Should().Contain(e => e.Message.Contains("productFeedback", StringComparison.Ordinal));
        logger.Entries.Should().NotContain(e => e.Message.Contains("callerGoalsName", StringComparison.Ordinal)
            || e.Message.Contains("callerFeedbackName", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendAsync_LogsNothingAboveInformationOnSuccess()
    {
        using var client = TestHelpers.CreateMockHttpClient("{\"results\":[]}");
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        await service.GetResourcesAsync("organizations");

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ApiKeyProvider_LogsAnErrorNamingTheSecret_WhenKeyVaultFails_AndStillThrows()
    {
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync("vitally-shared", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        var logger = new CapturingLogger<VitallyApiKeyProvider>();
        var provider = BuildProvider(secrets.Object, logger);

        var act = () => provider.GetApiKeyAsync();

        await act.Should().ThrowAsync<RequestFailedException>("the failure must still reach the caller");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Subject.Message.Should().Contain("vitally-shared");
        // The inverse of the tool-call rule, and deliberate: Azure's status and error code are what
        // tell a missing role from an unreachable vault, and they carry no secret or customer data.
        logger.Exceptions.Should().ContainSingle(e => e is RequestFailedException,
            "the Key Vault error is the diagnosis, so it is attached");
    }

    [Fact]
    public async Task ApiKeyProvider_LogsAKeyVaultTimeout()
    {
        // Azure.Core surfaces a timeout as TaskCanceledException with the CALLER's token not
        // cancelled — a slow vault, which is the case most worth seeing. A gate on the exception type
        // rather than the caller's token would silently drop it.
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync("vitally-shared", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("timed out"));
        var logger = new CapturingLogger<VitallyApiKeyProvider>();
        var provider = BuildProvider(secrets.Object, logger);

        var act = () => provider.GetApiKeyAsync(CancellationToken.None);

        await act.Should().ThrowAsync<TaskCanceledException>();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ApiKeyProvider_LogsAnError_WhenTheSecretHasNoValue()
    {
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync("vitally-shared", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(SecretModelFactory.KeyVaultSecret(new SecretProperties("vitally-shared"), value: null), Mock.Of<Response>()));
        var logger = new CapturingLogger<VitallyApiKeyProvider>();
        var provider = BuildProvider(secrets.Object, logger);

        var act = () => provider.GetApiKeyAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Subject.Message.Should().Contain("vitally-shared");
    }

    [Fact]
    public async Task ApiKeyProvider_DoesNotLogACallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync("vitally-shared", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var logger = new CapturingLogger<VitallyApiKeyProvider>();
        var provider = BuildProvider(secrets.Object, logger);

        var act = () => provider.GetApiKeyAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning,
            "a caller that went away is not a Key Vault fault");
    }

    public static TheoryData<string, LogLevel?> LevelCases => new()
    {
        { "upstream-status", LogLevel.Error },
        { "transport", LogLevel.Error },
        { "validation", LogLevel.Warning },
        { "unexpected", LogLevel.Error },
        // Audited already by AuditLogger.LogDenied, with the caller and the path.
        { "denied", null },
    };

    private static Exception ExceptionFor(string kind) => kind switch
    {
        "upstream-status" => new HttpRequestException(
            "Upstream API returned 500. Body: " + CustomerEmail, null, HttpStatusCode.InternalServerError),
        "transport" => new HttpRequestException("Connection refused " + CustomerEmail),
        "validation" => new ArgumentException("createdAfter must be ISO-8601 (got '" + CustomerEmail + "')"),
        "unexpected" => new InvalidOperationException("unexpected " + CustomerEmail),
        "denied" => new UnauthorizedAccessException("denied " + CustomerEmail),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(LevelCases))]
    public void ToolCallFailureLog_ChoosesTheLevel_AndNeverLogsTheMessage(string kind, LogLevel? expected)
    {
        var ex = ExceptionFor(kind);
        var logger = new CapturingLogger<object>();

        ToolCallFailureLog.Write(logger, "List_users", ex, "corr-123", CancellationToken.None);

        if (expected is null)
        {
            logger.Entries.Should().BeEmpty();
            return;
        }

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(expected.Value);
        entry.Message.Should().Contain("List_users").And.Contain(ex.GetType().Name).And.Contain("corr-123");
        entry.Message.Should().NotContain(CustomerEmail,
            "exception messages carry upstream bodies and caller input, so only the type is logged");
        // Attaching the exception would leak the same text by another route — the console formatter
        // and the OTel exporter both render it — and the message assertion above cannot see that.
        logger.Exceptions.Single().Should().BeNull("the exception is never attached to a tool-call failure");
    }

    [Fact]
    public void ToolCallFailureLog_RecordsTheUpstreamStatus()
    {
        var logger = new CapturingLogger<object>();

        ToolCallFailureLog.Write(logger, "List_users",
            new HttpRequestException("x", null, HttpStatusCode.TooManyRequests), "corr", CancellationToken.None);

        logger.Entries.Single().Message.Should().Contain("429");
    }

    [Fact]
    public async Task ToolCallFailureLog_IgnoresACallerCancellation_ButLogsATimeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var logger = new CapturingLogger<object>();

        ToolCallFailureLog.Write(logger, "List_users", new OperationCanceledException(), "corr", cts.Token);
        logger.Entries.Should().BeEmpty("the client went away; nothing failed");

        // An HttpClient timeout surfaces as TaskCanceledException with the caller's token NOT
        // cancelled — a slow upstream, which is exactly the failure worth seeing.
        ToolCallFailureLog.Write(logger, "List_users", new TaskCanceledException(), "corr", CancellationToken.None);
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void ToolCallFailureLog_NeverThrows_WhenTheLoggerDoes()
    {
        var act = () => ToolCallFailureLog.Write(new ThrowingLogger(), "List_users",
            new InvalidOperationException(), "corr", CancellationToken.None);

        act.Should().NotThrow("a failing log sink must not replace the tool's own result");
    }

    [Fact]
    public void ToolCallFailureLog_NeverThrows_WhenResolvingItsServicesDoes()
    {
        // The filter calls this on the way to returning the tool's own error result. A throw here — a
        // disposed request scope, say — would escape the filter and replace that result with the SDK's
        // generic message, which is the one thing this log must never do.
        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(It.IsAny<Type>())).Throws(new ObjectDisposedException("scope"));

        var act = () => ToolCallFailureLog.Write(services.Object, "List_users",
            new InvalidOperationException(), CancellationToken.None);

        act.Should().NotThrow();
    }

    private static VitallyApiKeyProvider BuildProvider(SecretClient secrets, ILogger<VitallyApiKeyProvider> logger) =>
        new(Options.Create(new VitallyServerOptions()), new MemoryCache(new MemoryCacheOptions()), logger, secrets);

    [Fact]
    public void ToolCallFailureLog_NeverThrows_WhenTheLoggerThrowsACancellation()
    {
        // Caught by type in an earlier version, and so let through: a cancellation from the SINK is not
        // the caller cancelling — that case is decided inside WriteCore, on the caller's token — so
        // excluding the type from the guard only let a logging failure replace the tool's result.
        var logger = new ThrowingLogger<object>(() => new OperationCanceledException("sink"));

        var viaLogger = () => ToolCallFailureLog.Write(logger, "List_users",
            new InvalidOperationException(), "corr", CancellationToken.None);
        viaLogger.Should().NotThrow();

        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(It.IsAny<Type>())).Throws(new OperationCanceledException("scope"));
        var viaServices = () => ToolCallFailureLog.Write(services.Object, "List_users",
            new InvalidOperationException(), CancellationToken.None);
        viaServices.Should().NotThrow();
    }

    [Fact]
    public async Task SendAsync_StillThrowsTheUpstreamFailure_WhenItsLoggerThrows()
    {
        // The HttpRequestException carries the Vitally body the client is shown. A throwing sink must
        // not replace it with its own exception, which the filter would not surface.
        using var client = TestHelpers.CreateMockHttpClient("{\"message\":\"externalId is required\"}",
            HttpStatusCode.BadRequest);
        var service = TestHelpers.BuildVitallyService(client, logger: new ThrowingLogger<VitallyService>());

        var act = () => service.GetResourcesAsync("organizations");

        (await act.Should().ThrowAsync<HttpRequestException>()).WithMessage("*externalId is required*");
    }

    [Fact]
    public async Task OrganizationSummary_KeepsItsSectionIsolation_WhenItsLoggerThrows()
    {
        // A section failure is absorbed into `{error: ...}` by design. Logging it must not undo that by
        // turning an absorbed error into a failed summary.
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(
            "{\"id\":\"org-1\",\"name\":\"Acme\"}",
            "{\"results\":[{\"id\":\"co-1\",\"name\":\"somethingElse\"}]}");
        using var _client = client;
        var service = TestHelpers.BuildVitallyService(client, logger: new ThrowingLogger<VitallyService>());

        var result = await service.GetOrganizationSummaryAsync("org-1", null, "goals", "feedback");

        result.Should().Contain("\"organization\"").And.Contain("not found");
    }

    [Fact]
    public async Task ApiKeyProvider_StillThrowsTheKeyVaultFailure_WhenItsLoggerThrows()
    {
        var secrets = new Mock<SecretClient>();
        secrets.Setup(s => s.GetSecretAsync("vitally-shared", null, It.IsAny<SecretContentType?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "Forbidden"));
        var provider = BuildProvider(secrets.Object, new ThrowingLogger<VitallyApiKeyProvider>());

        var act = () => provider.GetApiKeyAsync();

        await act.Should().ThrowAsync<RequestFailedException>("Azure's error is the diagnosis, not the sink's");
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("sink down");
    }

    /// <summary>A sink refusing writes, for the components that take a typed logger.</summary>
    private sealed class ThrowingLogger<T>(Func<Exception>? throws = null) : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw (throws?.Invoke() ?? new InvalidOperationException("sink down"));
    }
}
