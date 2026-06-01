namespace VitallyMcp;

/// <summary>
/// Controls the per-user audit trail. Because all users share one Vitally API key, Vitally's own
/// audit log can't attribute actions to individual FISCAL users — so the server emits its own
/// structured audit record (authenticated user + verb + resource + outcome) at the single point
/// every Vitally call funnels through. In production these records flow to Application Insights /
/// Log Analytics and are queryable by user.
/// </summary>
public class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>When true, emit an audit record for each Vitally action. Defaults to true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, read operations (HTTP GET) are also audited. Off by default because reads are
    /// high-volume; mutations (create/update/delete) and denied attempts are always recorded.
    /// </summary>
    public bool IncludeReads { get; set; }
}
