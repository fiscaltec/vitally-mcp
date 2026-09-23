using System.Security.Claims;
using System.Text.Json;
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
    /// A principal shaped like an Entra v2 access token: a pairwise <c>sub</c> that resolves to
    /// nobody, alongside the <c>oid</c> that does. The subject is synthetic — its only property that
    /// matters is being an opaque non-GUID, so a real one would add nothing but a checked-in
    /// user-specific string. The oid matches the constant the sibling authorisation tests already
    /// use, deliberately: these two suites must agree on what one caller looks like.
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
        // defeats the point of keeping one. The `oid` is the directory object id and resolves with
        // `az ad user show --id`. This is the defect the #108 validation found, before the
        // production flip could start writing such records.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pAiRwiSeSuBjEcTeXaMpLeVaLuE0000000000000"));

        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1", 200);

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba");
        message.Should().NotContain("pAiRwiSeSuBjEcT",
            "the pairwise subject is not resolvable, so recording it defeats attribution");
    }

    [Fact]
    public void CallerIdentity_DoesNotMineAnObjectIdOutOfAFederatedSubject()
    {
        // Regression guard for #156. The previous identity provider issued federated subjects shaped
        // `waad|connection|{objectId}`, and CallerIdentity used to split on '|' and take the trailing
        // GUID. Nothing mints that shape for this server any more, so the fallback was removed — and
        // reinstating it would resolve an object id out of a value an attacker-influenced token could
        // shape, for no gain. A token with no `oid` must resolve to null and the caller must fail
        // closed.
        var user = AuthenticatedUser(
            email: null, sub: "waad|fiscal-entra|675ebdda-7590-4d79-8ec3-a2d17ab029ba");

        CallerIdentity.TryGetObjectId(user).Should().BeNull(
            "only the oid claim names the directory object; a subject is not parsed for one");

        // The audit record still attributes, because AuditLogger's own raw-subject fallback catches
        // it — recording the whole opaque subject rather than a GUID extracted from it.
        var (audit, logger) = Build(user: user);
        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1", 200);

        logger.Entries.Should().ContainSingle().Subject.Message
            .Should().Contain("waad|fiscal-entra|675ebdda-7590-4d79-8ec3-a2d17ab029ba");
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
        var user = EntraV2User(oid, "S-1pAiRwiSeSuBjEcTeXaMpLeVaLuE0000000000000");
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
        var (audit, logger) = Build(user: AuthenticatedUser("alice@fiscaltec.com", "opaque-subject-123"));

        audit.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1?limit=20", 200);

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Information);
        message.Should().Contain("opaque-subject-123", "the stable subject id is the audit actor key");
        message.Should().NotContain("alice@fiscaltec.com", "email must not be written to the audit log");
        message.Should().Contain("DELETE");
        message.Should().Contain("/resources/accounts/acc-1");
        message.Should().NotContain("limit=20", "the query string must be stripped from the audit record");
    }

    /// <summary>
    /// The default is the control, so it is asserted on <see cref="AuditOptions"/> itself rather than
    /// through the helper above — which carries its own <c>includeReads: false</c> and would keep
    /// passing if the real default regressed. Reads are 56 of the 93 tools, and this is the only
    /// record of who accessed which customer record, so a false default means no meaningful trail.
    /// It was false until 2026-09-17 and no deployed target overrode it (#139).
    /// </summary>
    [Fact]
    public void IncludeReads_DefaultsToTrue()
    {
        new AuditOptions().IncludeReads.Should().BeTrue(
            "reads are the only record of who accessed which customer record; turning them off must be a deliberate choice");
    }

    [Fact]
    public void LogAction_SkipsReads_WhenIncludeReadsDisabled()
    {
        var (audit, logger) = Build(includeReads: false, user: AuthenticatedUser("alice@fiscaltec.com", "opaque-subject-123"));
        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/accounts", 200);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void LogAction_LogsReads_WhenIncludeReadsEnabled()
    {
        var (audit, logger) = Build(includeReads: true, user: AuthenticatedUser("alice@fiscaltec.com", "opaque-subject-123"));
        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/accounts", 200);
        logger.Entries.Should().ContainSingle();
    }

    [Fact]
    public void LogAction_NoOp_WhenDisabled()
    {
        var (audit, logger) = Build(enabled: false, user: AuthenticatedUser("alice@fiscaltec.com", "opaque-subject-123"));
        audit.LogAction(HttpMethod.Post, "https://rest.vitally-eu.io/resources/accounts", 201);
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public void LogDenied_RecordsWarning_WithUser()
    {
        var (audit, logger) = Build(user: AuthenticatedUser("bob@fiscaltec.com", "opaque-subject-999"));

        audit.LogDenied(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1");

        logger.Entries.Should().ContainSingle();
        var (level, message) = logger.Entries[0];
        level.Should().Be(LogLevel.Warning);
        message.Should().Contain("opaque-subject-999", "the stable subject id is the audit actor key");
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

        audit.LogToolCallDenied(AuthenticatedUser(null, "opaque-subject-123"), "Delete_account", "vitally:delete");

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

    [Fact]
    public void LogToolCall_ShowsThisUserCalledThisTool_AndWhichCustomersItTouched()
    {
        // The acceptance criterion for #147, stated as an outcome rather than a field list:
        // "the audit trail must show that THIS USER called THIS TOOL and accessed, modified or
        // deleted data for THESE CUSTOMERS". `List_organizations` is the case the upstream record
        // cannot answer — an unscoped list whose customers exist only in the response body.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pAiRwiSeSuBjEcTeXaMpLeVaLuE0000000000000"));

        var context = new ToolCallAuditContext();
        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-1"},{"id":"org-2"}]}"""));

        audit.LogToolCall(new ToolCallAudit(
            ToolName: "List_organizations",
            Arguments: AuditArguments.Format(null),
            Records: context.Summarise(),
            Outcome: "ok",
            Duration: TimeSpan.FromMilliseconds(120),
            CorrelationId: context.CorrelationId,
            PermissionTier: "vitally:read",
            TierServedStale: false,
            McpClient: "claude-code"));

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "this user");
        message.Should().Contain("List_organizations", "this tool");
        message.Should().Contain("org-1").And.Contain("org-2", "these customers");
    }

    private static ToolCallAudit SampleCall(
        string outcome = "ok",
        string tier = "vitally:read",
        bool tierStale = false) =>
        new(
            ToolName: "Search_users",
            Arguments: AuditArguments.Format(null),
            Records: new ToolCallAuditContext().Summarise(),
            Outcome: outcome,
            Duration: TimeSpan.FromMilliseconds(42),
            CorrelationId: "corr-1",
            PermissionTier: tier,
            TierServedStale: tierStale,
            McpClient: "claude-code");

    [Fact]
    public void LogToolCall_RecordsTheTierTheCallerResolvedTo_AndWhetherItWasStale()
    {
        // Entitlement is resolved live from Entra group membership, so it CANNOT be reconstructed
        // afterwards — once someone leaves a group, nothing can say what they were entitled to at the
        // time. And a tier served from the retained copy during a Graph outage is a weaker claim than
        // a fresh one; a record that cannot tell them apart overstates its own confidence.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        audit.LogToolCall(SampleCall(tier: "vitally:delete", tierStale: true));

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().Contain("vitally:delete", "the tier at the moment of the call");
        message.Should().Contain("tierStale=True", "a stale tier is a weaker claim and must say so");
    }

    [Fact]
    public void LogToolCall_RecordsAFailedCall_NotOnlyASuccessfulOne()
    {
        // A trail that records only successes cannot show an attempted deletion that errored, which
        // is precisely the kind of event an access record exists to hold.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        audit.LogToolCall(SampleCall(outcome: "error"));

        logger.Entries.Should().ContainSingle().Subject.Message.Should().Contain("outcome=error");
    }

    [Fact]
    public void LogToolCall_NeverRecordsTheCallersEmail()
    {
        // The policy reversal opened up tool arguments, not the caller's own identity attributes. The
        // object id resolves to a person with `az ad user show --id`, so the email adds nothing to
        // attribution and only widens what the trail discloses.
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim("oid", "675ebdda-7590-4d79-8ec3-a2d17ab029ba"),
                new Claim("preferred_username", "dsearle@fiscaltec.com"),
                new Claim(ClaimTypes.Email, "dsearle@fiscaltec.com"),
            },
            authenticationType: "Test"));
        var (audit, logger) = Build(user: user);

        audit.LogToolCall(SampleCall());

        logger.Entries.Should().ContainSingle().Subject.Message
            .Should().NotContain("fiscaltec.com", "the object id is the identifier, not the email");
    }

    [Fact]
    public void LogToolCall_ReportsArgumentTruncation_SeparatelyFromPagerTruncation()
    {
        // Two different facts that must not share a field. `truncated` says the PAGER stopped early,
        // so the matching total is unknown. Argument truncation says the recorded arguments are not
        // the full ones the caller sent. A record showing only the former would present a shortened
        // 2 KB filter as though it had been captured in full.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        var oversized = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonBody"] = new string('x', 9000) }))!;

        audit.LogToolCall(SampleCall() with { Arguments = AuditArguments.Format(oversized) });

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().Contain("argsTruncated=True", "the recorded arguments are not the full ones");
        message.Should().Contain("truncated=False", "the pager did not stop early — a different fact");
    }
}
