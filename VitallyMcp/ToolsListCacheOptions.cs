using ModelContextProtocol.Protocol;

namespace VitallyMcp;

/// <summary>
/// Cache hints advertised on tools/list, per the 2026-07-28 MCP spec. With 93 tools this is the
/// largest response the server emits, so letting clients cache it avoids re-sending the whole
/// catalogue every session.
///
/// <para>
/// The scope defaults to <see cref="CacheScope.Private"/> because the list is filtered per caller
/// once authorisation filtering is active — a shared cache would leak one tier's tool catalogue to
/// another. Only set <see cref="CacheScope.Public"/> if per-caller filtering is ever removed.
/// </para>
/// </summary>
public class ToolsListCacheOptions
{
    public const string SectionName = "ToolsListCache";

    /// <summary>Set false to advertise no cache hints at all (clients then re-list every session).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long clients may cache tools/list. Serialised as `ttlMs`.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cache scope. Serialised as `cacheScope` ("private"/"public").</summary>
    public CacheScope Scope { get; set; } = CacheScope.Private;

    public void Validate()
    {
        if (TimeToLive < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeToLive must not be negative (was {TimeToLive}).");
        }
    }
}
