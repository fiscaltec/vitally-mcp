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
/// Tri-state on purpose. <c>true</c>/<c>false</c> are reported by the resolver itself through
/// <see cref="ResolvedPermissions.ServedStale"/> (#161), so on the live path they are checked facts.
/// <c>null</c> means <b>not known</b> — the claim path, and a call admitted with RBAC bypassed
/// (<c>Authorization:Enabled=false</c> or <c>OAuth:NoAuth</c>), where the tier is <c>unresolved</c>
/// too. A call denied before any tier resolved never reaches this record: the SDK checkpoint refuses
/// it and only <c>LogToolCallDenied</c> is written.
/// It must never be collapsed into <c>false</c>: that would assert the tier was fresh when nothing
/// checked, a weaker claim dressed as a stronger one.
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
