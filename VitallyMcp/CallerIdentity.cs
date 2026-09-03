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
/// The <c>sub</c> fallback exists for Auth0-shaped subjects (<c>waad|connection|{objectId}</c>),
/// which embed the same object id — so the value returned here is the <i>same GUID</i> under either
/// provider. Historical Auth0-era audit records that stored the whole <c>sub</c> can therefore still
/// be joined to current ones by taking the trailing GUID.
/// </para>
/// </remarks>
public static class CallerIdentity
{
    /// <summary>
    /// The caller's Entra object id (a GUID), or <c>null</c> when none can be determined — the
    /// <c>oid</c> claim if present, else the trailing GUID of an Auth0-shaped <c>sub</c>.
    /// </summary>
    public static string? TryGetObjectId(ClaimsPrincipal? user)
    {
        if (user is null)
        {
            return null;
        }

        var oid = user.FindFirst("oid")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
        if (!string.IsNullOrWhiteSpace(oid) && Guid.TryParse(oid, out _))
        {
            return oid;
        }

        // JwtBearer's default inbound claim mapping renames "sub" to ClaimTypes.NameIdentifier, so
        // check both — otherwise the object id is never found in production and callers silently
        // fall back to whatever their own null-handling does.
        var sub = user.FindFirst("sub")?.Value
            ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(sub))
        {
            var last = sub.Split('|', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last is not null && Guid.TryParse(last, out _))
            {
                return last;
            }
        }

        return null;
    }
}
