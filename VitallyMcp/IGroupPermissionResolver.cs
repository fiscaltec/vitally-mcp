namespace VitallyMcp;

/// <summary>
/// Resolves the set of Vitally permissions a user currently holds from their <b>live</b> Entra
/// group membership, decoupling enforcement from the (frozen) token claim so group changes take
/// effect promptly. Implementations cache results briefly per user.
/// </summary>
public interface IGroupPermissionResolver
{
    /// <summary>
    /// Returns the permissions granted by the user's current group membership, or <c>null</c> if
    /// the lookup could not be performed (e.g. Graph unavailable) — in which case the caller should
    /// fall back to the token claim. An authenticated user who is in none of the configured groups
    /// resolves to an empty set (which denies), not null.
    /// </summary>
    /// <param name="userObjectId">The user's Entra object id (GUID).</param>
    Task<IReadOnlySet<string>?> TryResolvePermissionsAsync(string userObjectId, CancellationToken cancellationToken = default);
}
