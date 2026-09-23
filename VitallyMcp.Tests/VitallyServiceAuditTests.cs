using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

/// <summary>
/// Wiring tests for the audit trail's record-id capture (#147). These exist because the capture point
/// is easy to put in the wrong place and the wrong place looks correct.
/// </summary>
public class VitallyServiceAuditTests
{
    [Fact]
    public async Task GetResourcesAsync_RecordsTheReturnedIds_EvenWhenTheCallerProjectedThemAway()
    {
        // The trap: `GetResourcesAsync` applies `FilterJsonFields` *after* the upstream call, so a
        // caller passing `fields=name` receives results with no `id` at all. Capturing ids from the
        // tool's result rather than the raw response would therefore name nobody on exactly the calls
        // where a narrow projection was used — a silent gap that looks like a complete record.
        using var client = TestHelpers.CreateMockHttpClient(
            """{"results":[{"id":"org-1","name":"A"},{"id":"org-2","name":"B"}]}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildVitallyService(client, auditContext: context);

        var result = await service.GetResourcesAsync("organizations", fields: "name");

        result.Should().NotContain("org-1", "the caller asked for name only, so the result carries no ids");
        // NB `Equal(params string[])` would swallow a `because` argument as another expected
        // element, so the reason goes here: the audit record still names the customers read.
        context.Summarise().Ids.Should().Equal(["org-1", "org-2"]);
    }

    [Fact]
    public async Task GetByNameContainsAsync_MarksThePagerTruncation_SoTheRecordDoesNotImplyACompleteRead()
    {
        // `truncated` means the pager gave up at MaxAutoPageFetches, so the number of MATCHING
        // records is unknowable — Vitally's envelope exposes only `next`, there is no total. A record
        // that omitted this would imply RecordsFetched was everything that matched.
        var page = """{"results":[{"id":"org-1","name":"Acme"}],"next":"more"}""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page, page, page);
        using var _client = client;
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildVitallyService(client, auditContext: context, maxAutoPageFetches: 2);

        await service.GetByNameContainsAsync("organizations", "Acme");

        context.Summarise().Truncated.Should().BeTrue(
            "the page cap was hit before the result set was exhausted");
    }

    [Fact]
    public async Task DeleteResourceAsync_NamesTheCustomer_FromTheUrlWhenTheBodyCarriesNoId()
    {
        // The acceptance criterion is "accessed, MODIFIED OR DELETED data for these customers", and a
        // delete is the event an access record is most often kept for. Vitally answers one with a
        // bare acknowledgement, so the body yields no id and the primary record named nobody — even
        // though the id was right there in the request URL the whole time.
        using var client = TestHelpers.CreateMockHttpClient("""{"success":true,"message":"Deleted"}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildVitallyService(client, auditContext: context);

        await service.DeleteResourceAsync("accounts", "acc-1");

        var summary = context.Summarise();
        summary.Ids.Should().Equal(["acc-1"], "the record must name what was deleted");
        summary.CallsWithoutIds.Should().Be(0, "this is not a gap — the id was recoverable");
    }

    [Fact]
    public async Task ListCalls_DoNotInventAnIdFromTheCollectionName()
    {
        // The counterpart, and the reason this is method-scoped rather than a general URL fallback:
        // an unscoped list ends `/resources/organizations`, whose last segment is a collection name.
        // Treating that as a record id would put "organizations" in the trail as though it were a
        // customer — a fabricated identifier is far worse than an honest gap.
        using var client = TestHelpers.CreateMockHttpClient("""{"nothing":true}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildVitallyService(client, auditContext: context);

        await service.GetResourcesAsync("organizations");

        context.Summarise().Ids.Should().BeEmpty("no id is better than a made-up one");
    }

    [Fact]
    public async Task DeleteResourceAsync_NamesTheCustomer_EvenWhenTheBodyIsAnEmptyEnvelope()
    {
        // The URL fallback was gated on IdsAvailable, and an EMPTY result set is deliberately marked
        // available — that rule exists so a "no matches" search does not look like a broken record.
        // But a mutation answering with `{results:[]}` is an acknowledgement, not an empty search, so
        // the gate skipped the fallback and the delete named nobody again by a different route.
        using var client = TestHelpers.CreateMockHttpClient("""{"results":[]}""");
        var context = new ToolCallAuditContext();
        var service = TestHelpers.BuildVitallyService(client, auditContext: context);

        await service.DeleteResourceAsync("accounts", "acc-2");

        context.Summarise().Ids.Should().Equal(["acc-2"]);
    }
}
