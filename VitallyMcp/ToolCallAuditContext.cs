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
    int CallsWithoutIds,
    string PermissionTier,
    bool? TierServedStale)
{
    /// <summary>How many ids the cap allowed into the record — not how many records were read.</summary>
    public int IdsRecorded => Ids.Count;
}

/// <summary>
/// Gathers, for the span of one tool call, the records its upstream calls touched.
/// </summary>
public sealed class ToolCallAuditContext
{
    // One tool call is not one upstream call, and its upstream calls are not necessarily sequential:
    // GetOrganizationSummaryAsync starts its goals and product-feedback fetches before awaiting
    // either, so two responses aggregate here at once on this same scoped instance. Measured without
    // it, 500 concurrent records lost about five every run — silently, which is the worst way for an
    // audit trail to be wrong.
    private readonly Lock _gate = new();
    private readonly List<string> _ids = [];

    /// <summary>
    /// Ties this tool call's record to the upstream records it caused. Generated once per context —
    /// a value regenerated per read would join nothing to anything.
    /// </summary>
    public string CorrelationId { get; } = Guid.NewGuid().ToString("n");

    private int _recordsFetched;
    private bool _pagerTruncated;
    private int _callsWithoutIds;
    private string _permissionTier = "unresolved";
    private bool? _tierServedStale;

    public void RecordUpstream(AuditedRecords records)
    {
        lock (_gate)
        {
            // Cap the merged list as well as each response. Each response is already capped at 100,
            // but the auto-pager can make ten calls, so without this a single record carries a
            // thousand ids. Stop adding rather than trimming afterwards — the ids kept are then the
            // first touched, which is the order a reader would reconstruct the read in.
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
    }

    /// <summary>
    /// Records that the bounded auto-pager gave up at <c>Vitally:MaxAutoPageFetches</c>. Separate from
    /// the id cap on purpose: one says the total is unknown, the other says the list was shortened
    /// while the count stayed true.
    /// </summary>
    public void MarkPagerTruncated()
    {
        lock (_gate)
        {
            _pagerTruncated = true;
        }
    }

    /// <summary>
    /// Records the tier the authorizer actually resolved for this caller, at the moment of the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by <see cref="ToolAuthorizer"/> rather than re-derived here, for the same reason
    /// <see cref="CallerIdentity"/> is shared: the tier in the record must be the one the decision
    /// was made against, or the record can disagree with the decision it claims to document.
    /// </para>
    /// <para>
    /// ⚠️ <b>Unbackfillable.</b> Entitlement comes from live Entra group membership, so once someone
    /// leaves a group nothing can reconstruct what they were entitled to at a past moment.
    /// </para>
    /// <param name="servedStale">
    /// Whether the tier came from a retained copy during a Graph outage. <c>null</c> means
    /// <i>not known</i>, which is the current state:
    /// <see cref="GraphGroupPermissionResolver"/> serves stale internally and logs it, but does not
    /// report it back through <see cref="IGroupPermissionResolver"/>. Recording <c>false</c> here
    /// would assert the tier was fresh when nothing checked — a weaker claim dressed as a stronger
    /// one, which is the opposite of what this field is for.
    /// </param>
    /// </remarks>
    public void RecordResolvedTier(IReadOnlySet<string> permissions, bool? servedStale = null)
    {
        lock (_gate)
        {
            _permissionTier = permissions.Count == 0
                ? "none"
                : string.Join(",", permissions.OrderBy(p => p, StringComparer.Ordinal));
            _tierServedStale = servedStale;
        }
    }

    public ToolCallAuditSummary Summarise() => new(_ids, _recordsFetched, _pagerTruncated, _callsWithoutIds, _permissionTier, _tierServedStale);
}
