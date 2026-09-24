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

    [Fact]
    public void LogToolCall_NeutralisesAClientNameThatTriesToForgeAuditLines()
    {
        // `ClientInfo.Name` arrives in the caller's own per-request `_meta` — it is attacker
        // controlled, and it lands in a line-oriented log. A newline lets a client write what looks
        // like a second audit record, attributing an action to someone else entirely. This repo
        // already knows the pattern: `Program.cs` logs the authentication exception TYPE only, never
        // `Exception.Message`, because IdentityModel builds that from caller-controlled claims.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        var forged = "evil\r\nVitally audit: 00000000-0000-0000-0000-000000000000 called Delete_account";

        audit.LogToolCall(SampleCall() with { McpClient = forged });

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().NotContain("\n", "a client cannot start a new log line");
        message.Should().NotContain("\r", "nor a carriage return, which some readers treat the same way");
        message.Should().Contain("evil",
            "the name is recorded, defanged rather than dropped — hiding the attempt hides the attacker");
        message.Split('\n').Should().ContainSingle("the whole record stays one line");
    }

    [Fact]
    public void LogToolCall_CapsAnOverlongClientName()
    {
        // Tool arguments are explicitly bounded; this field had no bound at all, so a client could
        // inflate every record it made — on a stdout path that is about to become a billed telemetry
        // path.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        audit.LogToolCall(SampleCall() with { McpClient = new string('z', 5000) });

        logger.Entries.Should().ContainSingle().Subject.Message.Length
            .Should().BeLessThan(1000, "an unbounded client name must not inflate the record");
    }

    [Fact]
    public void LogAction_CarriesTheCorrelationId_SoTheUpstreamRecordsJoinToTheToolCall()
    {
        // The tool-call record is documented as carrying a correlation id that "ties the upstream
        // records to this one". That join only exists if the upstream records carry it too — and a
        // composite tool makes four of them, a paged one up to ten, so without this there is no way
        // to tell which upstream calls belonged to which tool call.
        var (audit, logger) = Build(includeReads: true, user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/organizations", 200, "corr-abc");

        logger.Entries.Should().ContainSingle().Subject.Message
            .Should().Contain("correlation=corr-abc");
    }

    [Fact]
    public void LogToolCall_NeutralisesUnicodeLineSeparatorsInTheClientName()
    {
        // `char.IsControl` does not classify U+2028/U+2029 as control characters, so the sanitiser
        // added for CR/LF let them straight through into a line-oriented log.
        var lineSeparator = ((char)0x2028).ToString();
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        audit.LogToolCall(SampleCall() with
        {
            McpClient = "evil" + lineSeparator + "Vitally audit: forged"
        });

        logger.Entries.Should().ContainSingle().Subject.Message
            .Should().NotContain(lineSeparator, "a line separator cannot start a forged record either");
    }

    /// <summary>An id carrying a real CR/LF, built from escapes so the file itself stays one line.</summary>
    private static readonly string Cr = ((char)13).ToString();
    private static readonly string Lf = ((char)10).ToString();

    /// <summary>An id carrying a real CR/LF, assembled from code points so this file stays parseable.</summary>
    private static readonly string ForgedId = "acc-1" + Cr + Lf + "Vitally audit: forged";

    [Fact]
    public void LogToolCall_NeutralisesLineBreaksInTheRecordIdsItReports()
    {
        // The third route for the same attack, and one I opened myself: FromMutationUrl decodes a
        // path segment, so a tool called with an id of `acc%0AVitally audit: forged` yields a real
        // newline that went straight into the line-oriented message. The client name and the
        // argument values were both sanitised; the ids were not.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        var context = new ToolCallAuditContext();
        context.RecordUpstream(new AuditedRecords(
            [ForgedId, new string('z', 5000)], 2, IdsAvailable: true));

        audit.LogToolCall(SampleCall() with { Records = context.Summarise() });

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().NotContain(Lf, "an id cannot start a new log line");
        message.Should().NotContain(Cr, "nor a carriage return");
        message.Should().NotContain(new string('z', 500), "nor can it bypass the size bound");
    }

    [Fact]
    public void LogToolCall_NeutralisesLineBreaksInTheToolName()
    {
        // The tool name comes from the caller's own `tools/call` params, so it is as
        // attacker-controlled as the client name and the ids — both of which are sanitised. A call
        // naming a tool that does not exist still reaches the audit filter, so an unresolvable name
        // carrying a newline forges a record. The fourth field of this shape, and the one the
        // "everything caller-controlled is flattened" rule was supposed to have covered.
        var (audit, logger) = Build(user: EntraV2User(
            oid: "675ebdda-7590-4d79-8ec3-a2d17ab029ba",
            pairwiseSub: "S-1pairwise"));

        audit.LogToolCall(SampleCall() with { ToolName = "List_users" + Cr + Lf + "Vitally audit: forged" });

        var message = logger.Entries.Should().ContainSingle().Subject.Message;
        message.Should().NotContain(Lf, "a tool name cannot start a new log line");
        message.Should().NotContain(Cr, "nor a carriage return");
    }

    [Fact]
    public void LogToolCall_CarriesTheCustomEventAttribute_SoTheRecordLandsInAppEventsNotAppTraces()
    {
        // The Azure Monitor exporter chooses the destination table by looking for ONE exact,
        // case-sensitive attribute key in the log state. Miss it or misspell it and the record is
        // written to AppTraces instead — silently, with no error and no warning — where it shares a
        // table with ordinary diagnostics and loses the per-table retention and access the audit
        // trail is being routed for. This test is the only thing that catches that before Azure does.
        var logger = new StateCapturingLogger<AuditLogger>();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(Options.Create(new AuditOptions { Enabled = true }), logger, accessor);

        audit.LogToolCall(SampleCall());

        var state = logger.States.Should().ContainSingle().Subject;
        state.Should().Contain(kv => kv.Key == "microsoft.custom_event.name",
            "this exact key is what routes the record to AppEvents");
        state.Single(kv => kv.Key == "microsoft.custom_event.name").Value.Should()
            .Be("VitallyToolCall", "the event name groups these records in the table");
    }

    /// <summary>An <see cref="ILoggerFactory"/> that hands out one capturing logger per category.</summary>
    private sealed class CapturingFactory : ILoggerFactory
    {
        public Dictionary<string, CapturingLogger<object>> Loggers { get; } = new(StringComparer.Ordinal);

        public ILogger CreateLogger(string categoryName)
        {
            if (!Loggers.TryGetValue(categoryName, out var logger))
            {
                logger = new CapturingLogger<object>();
                Loggers[categoryName] = logger;
            }

            return logger;
        }

        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    [Fact]
    public void LogToolCall_LeavesACustomerDataFreeBreadcrumb_ForWhenTheExporterLosesTheRecord()
    {
        // Once the records route to AppEvents the console is suppressed, and OpenTelemetry export is
        // ASYNCHRONOUS — an ingestion outage cannot throw back into the emitting call. So a lost
        // export would take the record with it, from AppEvents and from stdout both, which is exactly
        // what #147 forbids: "records degrade rather than disappear silently".
        //
        // The breadcrumb is the degraded form. It carries who, what and the correlation id — enough
        // to prove a call happened and to join it to the upstream records — and deliberately NO
        // arguments and NO record ids, because the console table is the one the data map declares
        // customer-data-free and #142's export is gated on that staying true.
        var factory = new CapturingFactory();
        var logger = new CapturingLogger<AuditLogger>();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, EmitBreadcrumb = true }), logger, accessor, factory,
            new KnownToolNames(["Search_users"]));

        var context = new ToolCallAuditContext();
        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-secret-1"}]}"""));

        audit.LogToolCall(SampleCall() with
        {
            Arguments = AuditArguments.Format(
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"query":"alice@example.com"}""")),
            Records = context.Summarise(),
        });

        var breadcrumb = factory.Loggers[AuditLogger.BreadcrumbCategory].Entries
            .Should().ContainSingle().Subject.Message;

        breadcrumb.Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "who");
        breadcrumb.Should().Contain("Search_users", "what");
        breadcrumb.Should().Contain("corr-1", "and how to join it to the upstream records");
        breadcrumb.Should().NotContain("alice@example.com", "no arguments on the console table");
        breadcrumb.Should().NotContain("org-secret-1", "and no customer record ids either");
    }

    [Fact]
    public void BreadcrumbCategory_IsNotAChildOfTheSuppressedCategory()
    {
        // `AddFilter` category rules are PREFIX matches. A rule on "VitallyMcp.AuditLogger" therefore
        // also matches "VitallyMcp.AuditLogger.Fallback" — so a breadcrumb under a child category is
        // suppressed from the console by the rule meant for the full record, AND from OpenTelemetry by
        // its own rule, and lands nowhere at all. The degradation path silently becomes no path.
        //
        // Asserted on the names rather than through a host because that is exactly where the bug
        // lives: the two constants have to be unrelated as strings, not merely different.
        AuditLogger.BreadcrumbCategory.Should().NotStartWith("VitallyMcp.AuditLogger",
            "a child category inherits the parent's suppression rule");
    }

    [Fact]
    public void LogToolCall_EmitsNoBreadcrumb_WhenTheExporterIsNotConfigured()
    {
        // ILoggerFactory is in DI on every host, so a breadcrumb keyed off its presence alone would
        // fire locally and in tests — where the console is NOT suppressed, giving two records per
        // call and contradicting the claim that the telemetry change is inert until configured.
        var factory = new CapturingFactory();
        var (auditLogger, _) = (new CapturingLogger<AuditLogger>(), 0);
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, EmitBreadcrumb = false }),
            auditLogger, accessor, factory);

        audit.LogToolCall(SampleCall());

        factory.Loggers.Should().BeEmpty("no breadcrumb logger is created when nothing suppresses the console");
    }

    public static TheoryData<string, Action<AuditLogger>> EveryAuditEmission() => new()
    {
        { "VitallyUpstreamCall", a => a.LogAction(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1", 200) },
        { "VitallyUpstreamDenied", a => a.LogDenied(HttpMethod.Delete, "https://rest.vitally-eu.io/resources/accounts/acc-1") },
        { "VitallyToolCallDenied", a => a.LogToolCallDenied(null, "Delete_account", "vitally:delete") },
    };

    [Theory]
    [MemberData(nameof(EveryAuditEmission))]
    public void EveryAuditRecord_CarriesTheCustomEventAttribute(string expectedEventName, Action<AuditLogger> emit)
    {
        // Program.cs suppresses the WHOLE VitallyMcp.AuditLogger category from stdout once the
        // exporter is configured — but only a record carrying microsoft.custom_event.name reaches
        // AppEvents. A record without it goes to AppTraces instead, so these three would have been
        // taken off the console AND kept out of the audit table: removed from the one place they were
        // visible, and landed in the shared diagnostics table with its own retention and access.
        //
        // LogAction is the sharpest case, because its resource path names the customer.
        var logger = new StateCapturingLogger<AuditLogger>();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, IncludeReads = true }), logger, accessor);

        emit(audit);

        var state = logger.States.Should().ContainSingle().Subject;
        state.Single(kv => kv.Key == "microsoft.custom_event.name").Value
            .Should().Be(expectedEventName, "every audit record belongs in AppEvents, not AppTraces");
    }

    [Theory]
    [MemberData(nameof(EveryAuditEmission))]
    public void EveryAuditRecord_LeavesABreadcrumb(string eventName, Action<AuditLogger> emit)
    {
        // The console suppression covers the WHOLE category, so every record needs a degradation
        // path, not just the tool-call one. Without this, a lost export takes a LogAction — the
        // record that names the customer by resource path — and nothing anywhere shows the call
        // happened.
        //
        // A theory rather than three more tests, because the failure mode here is forgetting one:
        // the same omission has now been made twice, once for the routing attribute and once for the
        // breadcrumb. A case per emission makes the next addition fail until it is covered.
        _ = eventName;
        var factory = new CapturingFactory();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, IncludeReads = true, EmitBreadcrumb = true }),
            new CapturingLogger<AuditLogger>(), accessor, factory);

        emit(audit);

        var breadcrumb = factory.Loggers[AuditLogger.BreadcrumbCategory].Entries
            .Should().ContainSingle().Subject.Message;

        breadcrumb.Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "who");
        breadcrumb.Should().NotContain("acc-1",
            "and still no customer identifiers — the resource path is exactly what must not follow it to stdout");
    }

    [Theory]
    [InlineData("alice@example.com")]
    [InlineData("acc-9f3c2b1a")]
    [InlineData("Get_account?query=bob@example.com")]
    [InlineData("Acme_123")]
    [InlineData("alice")]
    public void Breadcrumb_DoesNotCarryACallerInventedToolName(string hostileName)
    {
        // The tool name arrives in the caller's own tools/call params and the filter runs even for a
        // tool that does not exist — so a client can name one anything. Flatten stops it breaking the
        // line; it does nothing about the CONTENT. A tool named after a customer would therefore put
        // that identifier on the console stream, which is the one the data map declares
        // customer-data-free and #142's export is gated on.
        //
        // The full record in AppEvents keeps the name verbatim, where customer data is permitted and
        // access-controlled. Only the console copy is restricted.
        var factory = new CapturingFactory();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, EmitBreadcrumb = true }),
            new CapturingLogger<AuditLogger>(), accessor, factory,
            new KnownToolNames(["List_organizations", "Get_account"]));

        audit.LogToolCall(SampleCall() with { ToolName = hostileName });
        audit.LogToolCallDenied(null, hostileName, "vitally:delete");

        foreach (var entry in factory.Loggers[AuditLogger.BreadcrumbCategory].Entries)
        {
            entry.Message.Should().NotContain(hostileName,
                "only a REGISTERED tool name reaches the customer-data-free stream — a shape check "
                + "would pass Acme_123 and alice, which are exactly the identifiers at issue");
        }
    }

    [Fact]
    public void Breadcrumb_KeepsAToolNameThatLooksLikeOne()
    {
        // The restriction has to leave the breadcrumb useful: if an export is lost, this line is the
        // only place the tool is named at all.
        var factory = new CapturingFactory();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise")
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, EmitBreadcrumb = true }),
            new CapturingLogger<AuditLogger>(), accessor, factory,
            new KnownToolNames(["List_organizations", "Get_account"]));

        audit.LogToolCall(SampleCall() with { ToolName = "List_organizations" });

        factory.Loggers[AuditLogger.BreadcrumbCategory].Entries.Should().ContainSingle()
            .Subject.Message.Should().Contain("List_organizations");
    }

    [Fact]
    public void DenialBreadcrumb_AttributesToTheExplicitPrincipal_NotTheAmbientContext()
    {
        // LogToolCallDenied takes the principal deliberately: the SDK authorisation checkpoint hands
        // the policy's own principal, which is the authoritative identity and can exist with NO
        // ambient HttpContext. The breadcrumb resolved the actor from the accessor instead, so the
        // AppEvents record would name the caller while its degraded copy said "anonymous" — and the
        // two could not be joined, which is the one job the breadcrumb has when an export is lost.
        var factory = new CapturingFactory();
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, EmitBreadcrumb = true }),
            new CapturingLogger<AuditLogger>(),
            httpContextAccessor: null,
            loggerFactory: factory);

        audit.LogToolCallDenied(
            EntraV2User("675ebdda-7590-4d79-8ec3-a2d17ab029ba", "S-1pairwise"),
            "Delete_account",
            "vitally:delete");

        factory.Loggers[AuditLogger.BreadcrumbCategory].Entries.Should().ContainSingle()
            .Subject.Message.Should().Contain("675ebdda-7590-4d79-8ec3-a2d17ab029ba",
                "the breadcrumb must name whoever the full record names");
    }

    [Fact]
    public void Breadcrumb_UsesTheObjectIdOnly_NeverTheSubjectFallbacks()
    {
        // ResolveUserId falls back to the raw `sub` and then NameIdentifier when `oid` is absent —
        // deliberately, because a consistent-but-opaque key beats none in the FULL record. But those
        // are token-supplied strings: an unexpected token shape can carry an email, or a line break,
        // straight onto the console stream that is supposed to be customer-data-free and
        // injection-free. Every other caller-controlled field on that line is sanitised; the identity
        // was not.
        //
        // The breadcrumb takes the oid or nothing. An oid is a GUID by construction, so it cannot
        // carry either problem.
        var factory = new CapturingFactory();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("sub", "alice@example.com") }, authenticationType: "Test"))
            }
        };
        var audit = new AuditLogger(
            Options.Create(new AuditOptions { Enabled = true, EmitBreadcrumb = true, IncludeReads = true }),
            new CapturingLogger<AuditLogger>(), accessor, factory);

        audit.LogAction(HttpMethod.Get, "https://rest.vitally-eu.io/resources/organizations", 200);

        factory.Loggers[AuditLogger.BreadcrumbCategory].Entries.Should().ContainSingle()
            .Subject.Message.Should().NotContain("alice@example.com",
                "a token subject can be an email, and the console stream must not carry one");
    }
}
