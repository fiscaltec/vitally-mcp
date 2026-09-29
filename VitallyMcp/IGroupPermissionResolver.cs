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
    /// performed (e.g. Graph unavailable) — in which case the user must be treated as unresolvable.
    /// With <c>Authorization:LiveGroupCheck</c> on — every deployed target — that means <b>deny</b>:
    /// #108 removed the fall-through to the token claim, so null is not a downgrade path and must
    /// not be turned back into one. An authenticated user who is in none of the configured groups
    /// resolves to a result with an empty <see cref="ResolvedPermissions.Permissions"/> set (which
    /// also denies), not null.
    /// </summary>
    /// <param name="userObjectId">The user's Entra object id (GUID).</param>
    Task<ResolvedPermissions?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One answer from <see cref="IGroupPermissionResolver"/>: the permissions, and how far to trust
/// them. Returned as a single value so the two facts cannot be read at different moments — a
/// separate "was the last one stale?" query would invite exactly that drift (#161).
/// </summary>
/// <remarks>
/// Built only through <see cref="Confirmed"/> and <see cref="Retained"/>, never a public constructor.
/// <see cref="ServedStale"/> flows straight into the audit record, where <c>False</c> asserts Graph
/// checked the tier. A positional <c>bool</c> would let an implementer that never tracked staleness
/// write <c>false</c> as a reflex; naming the two cases makes each return site state which it is.
/// </remarks>
public sealed record ResolvedPermissions
{
    private ResolvedPermissions(IReadOnlySet<string> permissions, bool servedStale, TimeSpan age)
    {
        // A null set would surface as a NullReferenceException in the authorizer — still a denial,
        // but an unlogged one. Refuse it at the boundary instead.
        Permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        ServedStale = servedStale;
        Age = age;
    }

    /// <summary>
    /// Graph answered — on this call (<paramref name="age"/> zero), or within
    /// <c>LiveGroupCacheSeconds</c>. The live check working as designed, not a degradation.
    /// </summary>
    public static ResolvedPermissions Confirmed(IReadOnlySet<string> permissions, TimeSpan age) =>
        new(permissions, servedStale: false, age);

    /// <summary>
    /// A Graph lookup was attempted and failed, and this is the caller's retained last known-good set,
    /// served within <c>LiveGroupStaleSeconds</c>.
    /// </summary>
    public static ResolvedPermissions Retained(IReadOnlySet<string> permissions, TimeSpan age) =>
        new(permissions, servedStale: true, age);

    /// <summary>The permissions granted. Empty denies; it is not "unknown".</summary>
    public IReadOnlySet<string> Permissions { get; }

    /// <summary>
    /// Whether this came from <see cref="Retained"/>. A <i>checked</i> fact either way, which is what
    /// lets the audit record carry it as <c>True</c>/<c>False</c> rather than <c>unknown</c>.
    /// </summary>
    public bool ServedStale { get; }

    /// <summary>
    /// How old the answer is, measured from when the lookup that confirmed it <i>began</i> —
    /// deliberately an upper bound, since that start time is what the resolver stores ("erring old").
    /// Zero for a lookup made by this call. For a stale serve it is measured to the moment the lookup
    /// <i>failed</i>, so a slow failure is not under-reported. "Stale by 20 seconds" and "stale by 59
    /// minutes" are different claims.
    /// </summary>
    public TimeSpan Age { get; }
}
