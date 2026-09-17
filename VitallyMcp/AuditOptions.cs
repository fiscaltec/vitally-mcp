namespace VitallyMcp;

/// <summary>
/// Controls the per-user audit trail. Because all users share one Vitally API key, Vitally's own
/// audit log can't attribute actions to individual FISCAL users — so the server emits its own
/// structured audit record (authenticated user + verb + resource + outcome) at the single point
/// every Vitally call funnels through.
/// <para>
/// ⚠️ <b>These records do not reach Application Insights or Log Analytics, and never have</b> — an
/// earlier version of this comment said they did. Verified 2026-09-17: the workspace has local
/// (shared-key) authentication disabled, and the Container Apps log shipper authenticates with a
/// shared key, so it is refused. The records are written to stdout and retained nowhere. Tracked in
/// issue #142; design in <c>docs/superpowers/specs/2026-09-17-logging-observability-design.md</c>.
/// </para>
/// </summary>
public class AuditOptions
{
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
