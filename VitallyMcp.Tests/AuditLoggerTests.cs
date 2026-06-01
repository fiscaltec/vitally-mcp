using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class AuditLoggerTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static (AuditLogger audit, CapturingLogger<AuditLogger> logger) Build(
        bool enabled = true,
        bool includeReads = false,
        ClaimsPrincipal? user = null)
    {
        var logger = new CapturingLogger<AuditLogger>();
        var accessor = new HttpContextAccessor
        {
            HttpContext = user is null ? null : new DefaultHttpContext { User = user }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = enabled, IncludeReads = includeReads }),
            logger,
            accessor);
        return (audit, logger);
    }

    private static ClaimsPrincipal AuthenticatedUser(string? email, string sub) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim("sub", sub),
                new Claim("email", email ?? string.Empty)
            }.Where(c => !string.IsNullOrEmpty(c.Value)),
            authenticationType: "Test"));

    [Fact]
    public void LogAction_RecordsUserVerbAndResource_ForMutations()
    {
        var (audit, logger) = Build(user: AuthenticatedUser("alice@fiscaltec.com", "auth0|123"));

        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1?limit=20", 200);

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Information);
        message.Should().Contain("alice@fiscaltec.com");
        message.Should().Contain("auth0|123");
        message.Should().Contain("DELETE");
        message.Should().Contain("/resources/accounts/acc-1");
        message.Should().NotContain("limit=20", "the query string must be stripped from the audit record");
    }

    [Fact]
    public void LogAction_SkipsReads_ByDefault()
    {
        var (audit, logger) = Build(user: AuthenticatedUser("alice@fiscaltec.com", "auth0|123"));
        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/accounts", 200);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void LogAction_LogsReads_WhenIncludeReadsEnabled()
    {
        var (audit, logger) = Build(includeReads: true, user: AuthenticatedUser("alice@fiscaltec.com", "auth0|123"));
        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/accounts", 200);
        logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public void LogAction_NoOp_WhenDisabled()
    {
        var (audit, logger) = Build(enabled: false, user: AuthenticatedUser("alice@fiscaltec.com", "auth0|123"));
        audit.LogAction(HttpMethod.Post, "https://rest.vitally-eu.io/resources/accounts", 201);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void LogDenied_RecordsWarning_WithUser()
    {
        var (audit, logger) = Build(user: AuthenticatedUser("bob@fiscaltec.com", "auth0|999"));

        audit.LogDenied(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1");

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain("bob@fiscaltec.com");
        message.Should().Contain("DENIED");
    }

    [Fact]
    public void LogAction_FallsBackToAnonymous_WhenNoAuthenticatedUser()
    {
        var (audit, logger) = Build(includeReads: true, user: null);
        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/accounts", 200);
        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().Contain("anonymous");
    }
}
