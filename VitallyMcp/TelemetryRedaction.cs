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

/// <summary>
/// Removes caller-controlled text from spans before they are exported (#94, phase 6).
/// </summary>
/// <remarks>
/// <para>
/// Spans go to <c>AppRequests</c> / <c>AppDependencies</c>, a different store from the audit records,
/// with its own retention. Tool arguments are permitted in the audit record under its access control;
/// they are not permitted here. Three things put them here otherwise, each found by a probe of SDK 2.2.0:
/// </para>
/// <list type="bullet">
/// <item><b><see cref="Activity.StatusDescription"/></b>, on <i>every</i> span. On a failed tool call the
/// MCP SDK copies the whole error text into it, and that text carries the caller's own input
/// (<c>got 'alice@example.com'</c>) and Vitally's response body. The status code stays; the text goes.
/// The Azure Monitor exporter in 1.6.0 does not read the field, so this is defence against a future
/// exporter or distro version rather than a live leak.</item>
/// <item><b><c>gen_ai.tool.name</c> and the span name</b> on MCP spans. A <c>tools/call</c> naming a tool
/// that does not exist still produces a span carrying the invented name. Checked against
/// <see cref="KnownToolNames"/>, failing closed to <c>unrecognised</c>, as the audit breadcrumb and the
/// failure log already do.</item>
/// <item><b><c>jsonrpc.request.id</c></b> on MCP spans, which is whatever the client chose to send.</item>
/// </list>
/// <para>
/// ⚠️ A processor cannot rewrite <i>metric</i> tags, which is why the SDK's meter is not registered at all —
/// its <c>gen_ai.tool.name</c> dimension carries invented names too, unsampled. See <c>TelemetrySources</c>.
/// </para>
/// </remarks>
public sealed class SpanSanitisingProcessor(KnownToolNames knownTools) : BaseProcessor<Activity>
{
    public const string ToolNameTag = "gen_ai.tool.name";
    public const string RequestIdTag = "jsonrpc.request.id";
    public const string Unrecognised = "unrecognised";

    public override void OnEnd(Activity activity)
    {
        if (!string.IsNullOrEmpty(activity.StatusDescription))
        {
            activity.SetStatus(activity.Status);
        }

        if (activity.Source.Name != TelemetrySources.McpActivitySource)
        {
            return;
        }

        if (activity.GetTagItem(RequestIdTag) is not null)
        {
            activity.SetTag(RequestIdTag, null);
        }

        if (activity.GetTagItem(ToolNameTag) is string toolName && !knownTools.IsRegistered(toolName))
        {
            activity.SetTag(ToolNameTag, Unrecognised);

            // The SDK names a tool span "tools/call <name>". Rebuilt rather than string-replaced, so an
            // invented name that happens to contain the method name cannot survive the rewrite.
            var space = activity.DisplayName.IndexOf(' ');
            activity.DisplayName = space < 0 ? Unrecognised : activity.DisplayName[..space] + " " + Unrecognised;
        }
    }
}
