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
}
