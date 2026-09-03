using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class AuditLoggerTests
{
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

    /// <summary>
    /// A principal shaped like a real Entra v2 access token: a pairwise <c>sub</c> that resolves to
    /// nobody, alongside the <c>oid</c> that does. Values taken from an actual staging token decoded
    /// during the #108 cutover validation.
    /// </summary>
    private static ClaimsPrincipal EntraV2User(string oid, string pairwiseSub) =>
        new(new ClaimsIdentity(
            new[] { new Claim("oid", oid), new Claim("sub", pairwiseSub) },
            authenticationType: "Test"));

    [Fact]
    public void LogAction_AttributesToTheObjectId_NotThePairwiseSubject()
    {
        // An Entra v2 `sub` is unique per (user, application) and cannot be resolved to a person by
        // any Entra lookup, so an audit trail keyed on it is consistent but unattributable — which
        // is the whole point of keeping one. The `oid` is the directory object id and resolves with
        // `az ad user show --id`.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "pVp-4kbvcMPhQXMEBWjD53wuFatg-wJI6EqTzPD5C64"));

        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1", 200);

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba");
        message.Should().NotContain("pVp-4kbv",
            "the pairwise subject is not resolvable, so recording it defeats attribution");
    }

    [Fact]
    public void LogAction_AttributesAnAuth0SubjectToTheSameObjectId()
    {
        // The join across the cutover. An Auth0 federated subject embeds the same object id, so both
        // providers attribute to one value and a user's history is continuous rather than splitting
        // into two unrelated identifiers at the migration.
        var (audit, logger) = Build(user: AuthenticatedUser(
            email: null, sub: "waad|fiscal-entra|675ebdda-7590-4d79-8ec3-a2d17ab029ba"));

        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1", 200);

        logger.Entries.Should().ContainSingle().Subject.Message
            .Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba");
    }

    [Fact]
    public void LogAction_FallsBackToTheRawSubject_WhenNoObjectIdCanBeFound()
    {
        // A consistent-but-opaque key beats none. The fallback is what stops an unexpected token
        // shape attributing every action to "unknown", which would be worse than unresolvable.
        var (audit, logger) = Build(user: AuthenticatedUser(email: null, sub: "opaque-subject-42"));

        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1", 200);

        logger.Entries.Should().ContainSingle().Subject.Message.Should().Contain("opaque-subject-42");
    }

    [Fact]
    public void LogToolCallDenied_AttributesToTheSameIdentityTheAuthorizerResolved()
    {
        // The two must agree, or a denial record cannot be joined to the group membership that
        // caused it — which is why both go through CallerIdentity rather than each reading claims.
        var oid = "675ebdda-7590-4d79-8ec3-a2d17ab029ba";
        var user = EntraV2User(oid, "pVp-4kbvcMPhQXMEBWjD53wuFatg-wJI6EqTzPD5C64");
        var (audit, logger) = Build();

        audit.LogToolCallDenied(user, "Delete_account", "vitally:delete");

        logger.Entries.Should().ContainSingle().Subject.Message.Should().Contain(oid);
        CallerIdentity.TryGetObjectId(user).Should().Be(oid,
            "the authorizer resolves the same principal to the same value");
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
        message.Should().Contain("auth0|123", "the stable subject id is the audit actor key");
        message.Should().NotContain("alice@fiscaltec.com", "email must not be written to the audit log");
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
        message.Should().Contain("auth0|999", "the stable subject id is the audit actor key");
        message.Should().NotContain("bob@fiscaltec.com", "email must not be written to the audit log");
        message.Should().Contain("DENIED");
    }

    [Fact]
    public void LogToolCallDenied_RecordsSubjectToolAndRequiredPermission()
    {
        // No HttpContext at all: the principal comes from the authorisation policy, not the ambient
        // context, which is the whole reason this overload takes one.
        var (audit, logger) = Build(user: null);

        audit.LogToolCallDenied(
            AuthenticatedUser("carol@fiscaltec.com", "waad|entra|abc-123"),
            "Create_organization",
            "vitally:write");

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain("waad|entra|abc-123", "the stable subject id is the audit actor key");
        message.Should().NotContain("carol@fiscaltec.com", "email must not be written to the audit log");
        message.Should().Contain("DENIED");
        message.Should().Contain("Create_organization");
        message.Should().Contain("vitally:write");
    }

    [Fact]
    public void LogToolCallDenied_FallsBackToAnonymous_ForUnauthenticatedPrincipal()
    {
        var (audit, logger) = Build(user: null);

        audit.LogToolCallDenied(new ClaimsPrincipal(new ClaimsIdentity()), "Delete_account", "vitally:delete");

        logger.Entries.Should().ContainSingle();
        logger.Entries[0].Message.Should().Contain("anonymous");
    }

    [Fact]
    public void LogToolCallDenied_NoOp_WhenDisabled()
    {
        var (audit, logger) = Build(enabled: false);

        audit.LogToolCallDenied(AuthenticatedUser(null, "auth0|123"), "Delete_account", "vitally:delete");

        logger.Entries.Should().BeEmpty();
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
