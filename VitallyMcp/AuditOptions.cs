namespace VitallyMcp;

/// <summary>
/// Controls the per-user audit trail. Because all users share one Vitally API key, Vitally's own
/// audit log can't attribute actions to individual FISCAL users — so the server emits its own
/// structured audit record (authenticated user + verb + resource + outcome) at the single point
/// every Vitally call funnels through.
/// <para>
/// ⚠️ <b>Where these records go depends on one setting.</b>
/// <list type="bullet">
///   <item><b>With <c>ApplicationInsights__ConnectionString</c> set</b> — the Azure Monitor exporter
///     is registered and every record carries <c>microsoft.custom_event.name</c>, so they land in
///     <b><c>AppEvents</c></b>, where per-table retention and access apply. The whole
///     <c>VitallyMcp.AuditLogger</c> category is then suppressed from the console, and a
///     customer-data-free breadcrumb takes its place there (see
///     <see cref="EmitBreadcrumb"/>).</item>
///   <item><b>Without it</b> — no exporter, no suppression: the records go to stdout and are
///     <b>retained nowhere</b>, which is how it was for this server's entire prior lifetime. Verified
///     2026-09-17: the workspace has shared-key authentication disabled and the Container Apps log
///     shipper authenticates with a shared key, so that path was refused from the day the workspace
///     was created.</item>
/// </list>
/// <b>Production has that setting since 2026-09-25 (#147) — the first row is its live state, verified by
/// reading <c>VitallyToolCall</c> rows back out of <c>AppEvents</c> rather than inferred from a clean
/// deploy, since a wrong attribute routes to <c>AppTraces</c> silently. Staging does not have it, so the
/// second row is staging's live state.</b> Design in
/// <c>docs/superpowers/specs/2026-09-17-logging-observability-design.md</c>; the console-log export
/// it ungates is #142.
/// </para>
/// </summary>
public class AuditOptions
{
    /// <summary>
    /// Whether to emit the customer-data-free breadcrumb alongside each tool-call record.
    /// </summary>
    /// <remarks>
    /// Set in <c>Program.cs</c> only when the Azure Monitor exporter is configured, because the
    /// breadcrumb exists to survive an export that silently fails — and with no exporter there is
    /// nothing to survive, while the full record is still on the console. Keying it off
    /// <see cref="ILoggerFactory"/> being present instead would fire on every host, since DI always
    /// supplies one, and local runs would get two records per call.
    /// </remarks>
    public bool EmitBreadcrumb { get; set; }

    public const string SectionName = "Audit";

    /// <summary>When true, emit an audit record for each Vitally action. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, read operations (HTTP GET) are also audited. <b>Defaults to true</b>, and the
    /// default is the point: this is the only record of who accessed which customer record, and
    /// reads are 56 of the 93 tools, so a deployment that does not audit them has no meaningful
    /// trail at all. It defaulted to false until 2026-09-17, and because no deployed target ever
    /// set it, no read had ever been recorded.
    /// <para>Left configurable rather than removed so ingest volume stays controllable — reads are
    /// genuinely the high-volume path. Turning it off is a deliberate cost decision that trades away
    /// the attribution this class exists to provide; mutations and denials are recorded either way.
    /// Do not rely on a per-deployment override to switch it back on: a Container App recreate does
    /// not inherit environment variables, so coverage would lapse silently.</para>
    /// </summary>
    public bool IncludeReads { get; set; } = true;
}
