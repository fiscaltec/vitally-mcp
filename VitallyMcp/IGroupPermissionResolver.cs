namespace VitallyMcp;

/// <summary>
/// Resolves the set of Vitally permissions a user currently holds from their <b>live</b> Entra
/// group membership, decoupling enforcement from the (frozen) token claim so group changes take
/// effect promptly. Implementations cache results briefly per user.
/// </summary>
public interface IGroupPermissionResolver
{
    /// <summary>
    /// Returns the permissions granted by the user's current group membership, together with whether
    /// that answer was served from a retained copy, or <c>null</c> if the lookup could not be
    /// performed (e.g. Graph unavailable) — in which case the caller should treat the caller as
    /// unresolvable. With <c>Authorization:LiveGroupCheck</c> on — every deployed target — that
    /// means <b>deny</b>: #108 removed the fall-through to the token claim, so null is not a
    /// downgrade path and must not be turned back into one. An authenticated user who is in none of
    /// the configured groups resolves to an empty set (which also denies), not null.
    /// </summary>
    /// <param name="userObjectId">The user's Entra object id (GUID).</param>
    Task<ResolvedPermissions?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One answer from <see cref="IGroupPermissionResolver"/>: the permissions, and how far to trust
/// them. Returned as a single value so the two facts cannot be read at different moments — a
/// separate "was the last one stale?" query would invite exactly that drift (#161).
/// </summary>
/// <param name="Permissions">The permissions granted. Empty denies; it is not "unknown".</param>
/// <param name="ServedStale">
/// <c>true</c> when a Graph lookup was attempted, failed, and this is the caller's retained
/// last known-good set served within <c>LiveGroupStaleSeconds</c>. <c>false</c> when Graph answered
/// this call, or answered within <c>LiveGroupCacheSeconds</c> — the live check working as designed,
/// not a degradation. It is a <i>checked</i> fact either way, which is what lets the audit record
/// carry it as <c>True</c>/<c>False</c> rather than <c>unknown</c>.
/// </param>
/// <param name="Age">
/// How long before this answer was produced Graph last confirmed it. Zero for a lookup made by this
/// call. For a stale serve it is measured at the moment the lookup <i>failed</i>, so a slow failure
/// is not under-reported. "Stale by 20 seconds" and "stale by 59 minutes" are different claims.
/// </param>
public sealed record ResolvedPermissions(IReadOnlySet<string> Permissions, bool ServedStale, TimeSpan Age);
