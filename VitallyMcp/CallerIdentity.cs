using System.Security.Claims;

namespace VitallyMcp;

/// <summary>
/// Resolves the caller's Entra object id from their token claims.
/// </summary>
/// <remarks>
/// <para>
/// Shared by <see cref="ToolAuthorizer"/>, which uses it to decide entitlement, and
/// <see cref="AuditLogger"/>, which uses it to attribute the record. That sharing is the point
/// rather than a convenience: the identifier in an audit record has to be the same one the
/// authorisation decision was made against, or a denial cannot be joined to the group membership
/// that caused it.
/// </para>
/// <para>
/// <b>Why <c>oid</c> and not <c>sub</c>.</b> An Entra v2 token's <c>sub</c> is a <i>pairwise</i>
/// subject — unique per (user, application), opaque, and <b>not resolvable to a person</b>: there is
/// no Entra lookup that takes one. <c>oid</c> is the directory object id, stable across every
/// application in the tenant and resolvable with <c>az ad user show --id</c>, while carrying no more
/// personal data than the pairwise value does. Surfaced on 2026-09-03 by decoding a token from the
/// staging sign-in: its <c>sub</c> was a random-looking base64url string that nothing can attribute,
/// sitting alongside an <c>oid</c> that resolves in one command.
/// </para>
/// <para>
/// <b>There is no <c>sub</c> fallback, and reinstating one would be wrong.</b> An Entra <c>sub</c>
/// carries no object id in any form, so nothing could be recovered from it; a token with no
/// <c>oid</c> yields <c>null</c> here and the caller fails closed. The federated-subject fallback
/// this once had (<c>waad|connection|{objectId}</c>) existed for the previous identity provider,
/// and was removed with it in #156.
/// </para>
/// </remarks>
public static class CallerIdentity
{
    /// <summary>
    /// The caller's Entra object id (a GUID) from the <c>oid</c> claim, or <c>null</c> when the
    /// token carries none.
    /// </summary>
    public static string? TryGetObjectId(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        // Both spellings: JwtBearer's default inbound claim mapping rewrites some short claim names
        // to their WS-Federation URIs, and which one arrives depends on that mapping rather than on
        // the token. Checking only one finds nothing in production while passing every test that
        // mints the other.
        var oid = user.FindFirst("oid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        return !string.IsNullOrWhiteSpace(oid) && Guid.TryParse(oid, out _) ? oid : null;
    }
}
