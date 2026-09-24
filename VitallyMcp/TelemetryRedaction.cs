using System.Diagnostics;
using OpenTelemetry;

namespace VitallyMcp;

/// <summary>
/// Strips query strings from outbound-request spans before they are exported.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseAzureMonitor</c> turns on automatic <c>HttpClient</c> dependency collection, and this server
/// puts free-text search terms into Vitally query strings — <c>Search_users</c> passes its term as
/// <c>?query=&lt;value&gt;</c>, which is routinely a customer email. Those spans become
/// <c>AppDependencies</c> rows, a different table from the audit records with its own retention, and
/// <see cref="AuditLogger"/>'s careful <c>ResourcePath</c> stripping does nothing for them.
/// </para>
/// <para>
/// ⚠️ <b>This is defence in depth, not a fix for a live leak</b>, and the distinction is worth
/// keeping straight. Verified against the OpenTelemetry and <c>dotnet/runtime</c> sources for #162:
/// on .NET 9 and later the <b>runtime</b> sets <c>url.full</c> and redacts the query itself, so
/// <c>OpenTelemetry.Instrumentation.Http</c> does not set the attribute at all. The exported value is
/// already <c>…/users/search?*</c>.
/// </para>
/// <para>
/// It is still worth having, because that redaction is switchable: the AppContext switch
/// <c>System.Net.Http.DisableUriRedaction</c>, or <c>DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION</c>,
/// turns it off process-wide — the sort of thing set while debugging and left set. This makes the
/// guarantee local to this repository rather than inherited from a runtime default nothing here
/// controls, and it costs one string operation per outbound span.
/// </para>
/// </remarks>
public sealed class QueryStringRedactingProcessor : BaseProcessor<Activity>
{
    /// <summary>What a redacted query is replaced with, matching the runtime's own marker.</summary>
    public const string RedactionMarker = "?*";

    private static readonly string[] UrlAttributes = ["url.full", "http.url"];

    public override void OnEnd(Activity activity)
    {
        foreach (var name in UrlAttributes)
        {
            if (activity.GetTagItem(name) is not string value)
            {
                continue;
            }

            var redacted = Redact(value);
            if (!ReferenceEquals(redacted, value))
            {
                activity.SetTag(name, redacted);
            }
        }
    }

    /// <summary>
    /// Everything up to the first <c>?</c>, plus a marker. Returns the original instance untouched
    /// when there is nothing to strip, so the caller can skip the write.
    /// </summary>
    public static string Redact(string url)
    {
        var separator = url.IndexOf('?', StringComparison.Ordinal);
        if (separator < 0)
        {
            return url;
        }

        // Already redacted by the runtime — leave it rather than producing "?*​*".
        if (url.AsSpan(separator).SequenceEqual(RedactionMarker))
        {
            return url;
        }

        return string.Concat(url.AsSpan(0, separator), RedactionMarker);
    }
}
