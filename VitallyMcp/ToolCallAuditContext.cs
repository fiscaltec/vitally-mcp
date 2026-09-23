namespace VitallyMcp;

/// <summary>What one tool call touched, gathered from every upstream call it made.</summary>
/// <param name="Truncated">
/// <c>true</c> when the bounded auto-pager stopped before exhausting the result set, which makes the
/// number of <i>matching</i> records unknowable — Vitally's envelope exposes only <c>next</c>, so
/// there is no total. Deliberately <b>not</b> the same as the id list being capped.
/// </param>
public readonly record struct ToolCallAuditSummary(
    IReadOnlyList<string> Ids,
    int RecordsFetched,
    bool Truncated,
    int CallsWithoutIds)
{
    /// <summary>How many ids the cap allowed into the record — not how many records were read.</summary>
    public int IdsRecorded => Ids.Count;
}

/// <summary>
/// Gathers, for the span of one tool call, the records its upstream calls touched.
/// </summary>
public sealed class ToolCallAuditContext
{
    private readonly List<string> _ids = [];

    /// <summary>
    /// Ties this tool call's record to the upstream records it caused. Generated once per context —
    /// a value regenerated per read would join nothing to anything.
    /// </summary>
    public string CorrelationId { get; } = Guid.NewGuid().ToString("n");

    private int _recordsFetched;
    private bool _pagerTruncated;
    private int _callsWithoutIds;

    public void RecordUpstream(AuditedRecords records)
    {
        // Cap the merged list as well as each response. Each response is already capped at 100, but
        // the auto-pager can make ten calls, so without this a single record carries a thousand ids.
        // Stop adding rather than trimming afterwards — the ids kept are then the first touched,
        // which is the order a reader would reconstruct the read in.
        foreach (var id in records.Ids)
        {
            if (_ids.Count >= AuditRecordIds.MaxIds)
            {
                break;
            }

            _ids.Add(id);
        }

        _recordsFetched += records.RecordsFetched;

        // State the gap rather than absorbing it. A tool making four upstream calls of different
        // shapes would otherwise produce a record that looks like a complete account of what was
        // touched while silently missing one of them.
        if (!records.IdsAvailable)
        {
            _callsWithoutIds++;
        }
    }

    /// <summary>
    /// Records that the bounded auto-pager gave up at <c>Vitally:MaxAutoPageFetches</c>. Separate from
    /// the id cap on purpose: one says the total is unknown, the other says the list was shortened
    /// while the count stayed true.
    /// </summary>
    public void MarkPagerTruncated() => _pagerTruncated = true;

    public ToolCallAuditSummary Summarise() => new(_ids, _recordsFetched, _pagerTruncated, _callsWithoutIds);
}
