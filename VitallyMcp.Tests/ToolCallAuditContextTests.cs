using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

/// <summary>
/// One tool call can make several upstream calls — <c>Get_organization_summary</c> makes four, and the
/// bounded auto-pager can make ten. The audit record is per <i>tool call</i>, so something has to
/// gather what those upstream calls touched and hand it back as one answer.
/// </summary>
public class ToolCallAuditContextTests
{
    [Fact]
    public void Summarise_AggregatesTheRecordsTouchedAcrossEveryUpstreamCall()
    {
        var context = new ToolCallAuditContext();

        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-1"},{"id":"org-2"}]}"""));
        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"goal-7"}]}"""));

        var summary = context.Summarise();

        summary.Ids.Should().Equal("org-1", "org-2", "goal-7");
        summary.RecordsFetched.Should().Be(3);
    }

    [Fact]
    public void Summarise_CapsTheMergedIdList_WhileTheFetchedCountStaysTrue()
    {
        // Each upstream response is capped at 100 ids, but the auto-pager can make ten calls — so
        // without a second cap here a single record carries a thousand ids. The fetched count is what
        // keeps the magnitude honest once the list is cut.
        var context = new ToolCallAuditContext();
        for (var page = 0; page < 10; page++)
        {
            var records = string.Join(",", Enumerable.Range(0, 100).Select(i => $$"""{"id":"org-{{page}}-{{i}}"}"""));
            context.RecordUpstream(AuditRecordIds.Extract($$"""{"results":[{{records}}]}"""));
        }

        var summary = context.Summarise();

        summary.Ids.Should().HaveCount(100, "the merged list is capped as well as each response");
        summary.RecordsFetched.Should().Be(1000, "the true magnitude of the read survives the cap");
    }

    [Fact]
    public void Summarise_DistinguishesAPagerTruncation_FromTheIdCap()
    {
        // These mean different things and must not be conflated. `Truncated` says the pager stopped
        // at MaxAutoPageFetches, so the number of *matching* records is unknowable — Vitally's
        // envelope exposes only `next`, there is no total. The id cap says the list was shortened
        // while the count stayed true. "Fetched 1000, ids 100, truncated" is honest; a capped read
        // claiming the total is unknown, or a truncated one implying RecordsFetched was all of it,
        // is not.
        var cappedButComplete = new ToolCallAuditContext();
        var page = string.Join(",", Enumerable.Range(0, 200).Select(i => $$"""{"id":"org-{{i}}"}"""));
        cappedButComplete.RecordUpstream(AuditRecordIds.Extract($$"""{"results":[{{page}}]}"""));

        cappedButComplete.Summarise().Truncated.Should().BeFalse(
            "the id list was capped, but the pager read everything that matched");

        var pagerStopped = new ToolCallAuditContext();
        pagerStopped.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-1"}]}"""));
        pagerStopped.MarkPagerTruncated();

        pagerStopped.Summarise().Truncated.Should().BeTrue(
            "the pager gave up early, so the matching total is unknown");
    }

    [Fact]
    public void CorrelationId_IsStableWithinOneToolCall_AndDistinctBetweenThem()
    {
        var first = new ToolCallAuditContext();
        var second = new ToolCallAuditContext();

        first.CorrelationId.Should().NotBeNullOrWhiteSpace();
        first.CorrelationId.Should().Be(first.CorrelationId,
            "every upstream record from one tool call must carry the same id, or they cannot be joined");
        first.CorrelationId.Should().NotBe(second.CorrelationId,
            "two tool calls sharing an id would join one caller's reads to another's");
    }

    [Fact]
    public void Summarise_ReportsUpstreamCallsThatYieldedNoIds()
    {
        // `Get_organization_summary` makes four upstream calls of different shapes. If one of them
        // returns something this cannot read ids from, the record has to say so — otherwise it looks
        // like a complete account of what was touched while silently missing a call.
        var context = new ToolCallAuditContext();
        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-1"}]}"""));
        context.RecordUpstream(AuditRecordIds.Extract("""{"deleted":true}"""));

        var summary = context.Summarise();

        summary.Ids.Should().Equal("org-1");
        summary.CallsWithoutIds.Should().Be(1, "the gap is stated rather than absorbed");
    }

    [Fact]
    public void Summarise_CarriesTheTierTheAuthorizerResolved_AndLeavesStalenessUnknownUntilItIsKnown()
    {
        // The tier has to come from the component that made the decision, not from a second lookup —
        // or the record could disagree with the decision it purports to document.
        var context = new ToolCallAuditContext();

        context.RecordResolvedTier(new HashSet<string> { "vitally:write", "vitally:read" });

        var summary = context.Summarise();
        summary.PermissionTier.Should().Be("vitally:read,vitally:write", "sorted, so records compare");
        summary.TierServedStale.Should().BeNull(
            "GraphGroupPermissionResolver does not yet report whether it served a retained copy, and "
            + "recording false would assert the tier was fresh when nothing checked");
    }

    [Fact]
    public async Task RecordUpstream_IsSafeWhenOneToolFetchesConcurrently()
    {
        // Not hypothetical: `GetOrganizationSummaryAsync` starts its goals and product-feedback
        // fetches before awaiting either, so two upstream responses enter this aggregation at once —
        // on the SAME scoped context, because they belong to one tool call. An unsynchronised
        // `List<T>` and `int++` lose records or throw, and the tool it breaks is the one whose record
        // matters most.
        var context = new ToolCallAuditContext();
        var oneRecord = AuditRecordIds.Extract("""{"results":[{"id":"org-1"}]}""");

        await Task.WhenAll(Enumerable.Range(0, 500)
            .Select(_ => Task.Run(() => context.RecordUpstream(oneRecord))));

        var summary = context.Summarise();
        summary.RecordsFetched.Should().Be(500, "no increment may be lost");
        summary.Ids.Count.Should().BeLessThanOrEqualTo(100, "the cap must hold under concurrency too");
    }

    [Fact]
    public void Summarise_IsASnapshot_NotAWindowOntoTheLiveList()
    {
        // The filter calls Summarise while upstream calls may still be in flight, and then
        // enumerates the ids to write them. Handing back the live list means the record can change
        // under the writer — or throw mid-enumeration — so the summary has to be a copy.
        //
        // Deterministic on purpose: an earlier attempt at this drove concurrent tasks and passed
        // against the unfixed code, because the assertion never enumerated and the id cap closed the
        // race window after 100 adds. A test that cannot fail is worse than no test.
        var context = new ToolCallAuditContext();
        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-1"}]}"""));

        var taken = context.Summarise();
        context.RecordUpstream(AuditRecordIds.Extract("""{"results":[{"id":"org-2"}]}"""));

        taken.Ids.Should().Equal(["org-1"], "a summary already taken must not change afterwards");
        taken.IdsRecorded.Should().Be(1);
        context.Summarise().Ids.Should().Equal(["org-1", "org-2"], "while a fresh one sees both");
    }
}
