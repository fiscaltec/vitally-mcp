namespace VitallyMcp;

/// <summary>
/// One tool call, as the audit trail records it.
/// </summary>
/// <param name="ToolName">The MCP tool invoked — captured only on denial before #147.</param>
/// <param name="Arguments">The scope the caller asked for, rendered and bounded.</param>
/// <param name="Records">What the call touched, gathered across its upstream calls.</param>
/// <param name="Outcome">Whether the call succeeded.</param>
/// <param name="Duration">Per-tool latency, in one place with the rest of the record.</param>
/// <param name="CorrelationId">Ties the upstream records to this one.</param>
/// <param name="PermissionTier">
/// The tier the caller actually resolved to <b>at the moment of the call</b>. Unbackfillable:
/// <c>LiveGroupCheck</c> resolves entitlement from live Entra group membership, so once someone
/// leaves a group there is no reconstructing what they were entitled to. Without this the trail
/// cannot answer "was this person entitled to do that at the time?".
/// </param>
/// <param name="TierServedStale">
/// <c>true</c> when that tier came from <c>GraphGroupPermissionResolver</c>'s retained copy during a
/// Graph outage rather than a fresh lookup. A stale tier is a weaker claim than a fresh one, and a
/// record that cannot tell them apart overstates its own confidence.
/// <para>
/// <c>null</c> means <b>not known</b>, and that is what it holds today: the resolver serves stale
/// internally and logs it, but does not report it back through <c>IGroupPermissionResolver</c>.
/// Recording <c>false</c> would assert the tier was fresh when nothing checked.
/// </para>
/// </param>
/// <param name="McpClient">
/// Which client made the call. Read per call from the request's <c>_meta</c> — in stateless mode
/// there is no <c>initialize</c> handshake to read <c>clientInfo</c> from, since MCP 2026-07-28
/// removed it.
/// </param>
public readonly record struct ToolCallAudit(
    string ToolName,
    AuditedArguments Arguments,
    ToolCallAuditSummary Records,
    string Outcome,
    TimeSpan Duration,
    string CorrelationId,
    string PermissionTier,
    bool? TierServedStale,
    string? McpClient);
