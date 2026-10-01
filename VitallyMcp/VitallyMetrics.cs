using System.Diagnostics.Metrics;

namespace VitallyMcp;

/// <summary>
/// The telemetry source and meter names this server deals with (#94, phase 6) — two registered with the
/// Azure Monitor exporter, and one (<see cref="McpMeter"/>) deliberately not. One place, so
/// <c>Program.cs</c> and the tests read the same strings: a name nothing publishes is accepted silently
/// and captures nothing. The composed test proves <see cref="McpActivitySource"/> is real; the exporter
/// test pins which are registered.
/// </summary>
public static class TelemetrySources
{
    /// <summary>The MCP SDK's <see cref="System.Diagnostics.ActivitySource"/>: a span per request, tool calls included.</summary>
    /// <remarks>
    /// ⚠️ <c>Experimental.</c>-prefixed in SDK 2.2.0, like the meter — <b>not</b> the bare
    /// <c>ModelContextProtocol</c> that #94's issue text gave. Registering that name would have been
    /// accepted silently and captured no MCP span at all; the composed test caught it. Expect the
    /// prefix to be dropped when the SDK stabilises its telemetry, and the test to say so.
    /// </remarks>
    public const string McpActivitySource = "Experimental.ModelContextProtocol";

    /// <summary>
    /// The MCP SDK's meter — deliberately <b>NOT</b> registered. Kept here so a test can assert it stays
    /// that way.
    /// </summary>
    /// <remarks>
    /// Its <c>mcp.server.operation.duration</c> histogram carries <c>gen_ai.tool.name</c> as a dimension,
    /// and a <c>tools/call</c> naming a tool that does not exist puts the invented name there: caller text,
    /// unbounded cardinality, and — metrics being unsampled — every one recorded. A span processor can
    /// rewrite a span's tags but not a metric's. Per-tool latency is already queryable, unsampled, from
    /// <c>AuditDurationMs</c> on the <c>VitallyToolCall</c> record in <c>AppEvents</c>. If per-method
    /// failure rates are ever wanted, register it behind a view that drops <c>gen_ai.tool.name</c>,
    /// never as-is — see CLAUDE.md.
    /// </remarks>
    public const string McpMeter = "Experimental.ModelContextProtocol";

    /// <summary>This server's own counters; see <see cref="VitallyMetrics"/>.</summary>
    public const string VitallyMeter = VitallyMetrics.MeterName;
}

/// <summary>
/// The server's own counters (#94, phase 6), on one <see cref="Meter"/> named <see cref="MeterName"/>.
/// </summary>
/// <remarks>
/// <para>
/// Metrics rather than log lines, because these are questions about <i>rates</i>: how often the server
/// exceeds Vitally's 1000 requests a minute, how often the auto-pager gives up, and how often each cache
/// spares a round-trip. A counter is pre-aggregated and carries dimensions, where a log line is message
/// text to parse. And metrics are never sampled, so these stay exact however far trace sampling is
/// reduced. (The cache rate counts lookups, not requests — see the caveats in CLAUDE.md.)
/// </para>
/// <para>
/// ⚠️ <b>Tags carry only values this code fixes</b> — a cache name, a hit/miss, a resource type the
/// tool layer chose. Never a caller argument, an id or an exception message: metric dimensions are
/// stored for every distinct value, and a caller-controlled one is both a cardinality explosion and a
/// way to write customer data into a store the audit rules never considered.
/// </para>
/// <para>
/// Created through <see cref="IMeterFactory"/> so the host owns the meter's lifetime and a test can
/// observe one instance without seeing another test's measurements. Every consumer takes it as an
/// optional constructor parameter, so the components still build without it in tests that do not care.
/// </para>
/// </remarks>
public sealed class VitallyMetrics
{
    /// <summary>The meter name to register with the OpenTelemetry meter provider.</summary>
    public const string MeterName = "VitallyMcp";

    private readonly Counter<long> _rateLimitRetries;
    private readonly Counter<long> _rateLimitExhausted;
    private readonly Counter<long> _pagerTruncations;
    private readonly Counter<long> _cacheLookups;

    public VitallyMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _rateLimitRetries = meter.CreateCounter<long>(
            "vitally.ratelimit.retries", unit: "{retry}",
            description: "Retries after a 429 from Vitally.");
        _rateLimitExhausted = meter.CreateCounter<long>(
            "vitally.ratelimit.exhausted", unit: "{request}",
            description: "Requests whose 429 retries ran out, so the 429 reached the caller.");
        _pagerTruncations = meter.CreateCounter<long>(
            "vitally.autopager.truncations", unit: "{call}",
            description: "Server-side filtered calls that stopped at Vitally:MaxAutoPageFetches before exhausting the endpoint.");
        _cacheLookups = meter.CreateCounter<long>(
            "vitally.cache.lookups", unit: "{lookup}",
            description: "Cache lookups, by cache and by hit or miss.");
    }

    public void RateLimitRetry() => _rateLimitRetries.Add(1);

    public void RateLimitExhausted() => _rateLimitExhausted.Add(1);

    /// <summary>Counts a server-side filtered call that stopped at the page cap.</summary>
    /// <param name="resourceType">
    /// The fixed resource kind — the tool layer's defaults key, already checked against the allowlist
    /// by the caller. Never the paged path, which can carry the caller's id.
    /// </param>
    public void PagerTruncated(string resourceType) =>
        _pagerTruncations.Add(1, new KeyValuePair<string, object?>("resource", resourceType));

    /// <summary>Counts one cache lookup, as a hit (the round-trip was spared) or a miss.</summary>
    /// <param name="cache">One of <c>api_key</c>, <c>group_membership</c>, <c>oidc_discovery</c>.</param>
    /// <param name="hit">Whether the cache answered without the round-trip.</param>
    public void CacheLookup(string cache, bool hit) =>
        _cacheLookups.Add(1,
            new KeyValuePair<string, object?>("cache", cache),
            new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));
}
