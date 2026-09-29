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
            "{\"message\":\"no user " + CustomerEmail + "\"}", HttpStatusCode.BadRequest);
        var logger = new CapturingLogger<VitallyService>();
        var service = TestHelpers.BuildVitallyService(client, logger: logger);

        // A sentinel without reserved characters, so URL-encoding cannot disguise a leak and let the
        // assertion below pass for the wrong reason.
        const string searchTerm = "searchtermsentinel";
        var act = () => service.GetRawAsync("users/search",
            new Dictionary<string, string> { ["query"] = searchTerm });
        await act.Should().ThrowAsync<HttpRequestException>();

        var entry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject.Message;
        entry.Should().Contain("400").And.Contain("GET").And.Contain("/resources/users/search");
        entry.Should().NotContain(CustomerEmail, "the upstream body may be customer data");
        entry.Should().NotContain(searchTerm, "the query string carries caller search terms");
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

    private static VitallyApiKeyProvider BuildProvider(SecretClient secrets, ILogger<VitallyApiKeyProvider> logger) =>
        new(Options.Create(new VitallyServerOptions()), new MemoryCache(new MemoryCacheOptions()), logger, secrets);

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("sink down");
    }
}
