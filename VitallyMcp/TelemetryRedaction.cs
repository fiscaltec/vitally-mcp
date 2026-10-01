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
/// Spans go to <c>AppRequests</c> / <c>AppDependencies</c> — different tables from the audit records,
/// with their own retention. Tool arguments are permitted in the audit record under its access control;
/// they are not permitted here. Probes of SDK 2.2.0 found caller text reaching spans by seven routes,
/// three in the first review and four more in its re-run, so the MCP branch is built on allowlists:
/// </para>
/// <list type="bullet">
/// <item><b><see cref="Activity.StatusDescription"/></b>, on <i>every</i> span. On a failed tool call the
/// MCP SDK copies the whole error text into it, and that text carries the caller's own input
/// (<c>got 'alice@example.com'</c>) and Vitally's response body. The status code stays; the text goes.
/// The Azure Monitor exporter in 1.6.0 does not appear to read the field — inferred from its metadata,
/// not observed — so this is defence against a future exporter or distro version rather than a known
/// live leak.</item>
/// <item><b>The method</b> on MCP spans: kept only if the SDK defines it (<see cref="KnownMethods"/>), so an
/// unknown method or notification is <c>unrecognised</c>.</item>
/// <item><b>The tool name</b>: kept only if it is in <see cref="KnownToolNames"/>, failing closed to
/// <c>unrecognised</c>, as the audit breadcrumb and the failure log already do.</item>
/// <item><b>Every other MCP tag</b> — <c>jsonrpc.request.id</c>, <c>mcp.resource.uri</c>,
/// <c>gen_ai.prompt.name</c> among them — is dropped unless it is in <see cref="AllowedMcpTags"/>.</item>
/// <item><b>The span name</b> is rebuilt from the cleaned method and tool, never patched, since the SDK
/// appends whatever target the caller named.</item>
/// </list>
/// <para>
/// It must run before the exporter's processors; the exporter test pins that order.
/// </para>
/// <para>
/// ⚠️ A processor cannot rewrite <i>metric</i> tags, which is why the SDK's meter is not registered at all —
/// its <c>gen_ai.tool.name</c> dimension carries invented names too, unsampled. See <c>TelemetrySources</c>.
/// </para>
/// </remarks>
public sealed class SpanSanitisingProcessor(KnownToolNames knownTools) : BaseProcessor<Activity>
{
    public const string ToolNameTag = "gen_ai.tool.name";
    public const string MethodTag = "mcp.method.name";
    public const string RequestIdTag = "jsonrpc.request.id";
    public const string Unrecognised = "unrecognised";

    /// <summary>
    /// The MCP span tags that may be exported. <b>An allowlist, deliberately</b>: a re-review probe of SDK
    /// 2.2.0 found caller text in <c>mcp.resource.uri</c> and <c>gen_ai.prompt.name</c> after the first
    /// version had blocked only the routes known then, and a future SDK can add another. Anything not
    /// listed here is dropped. <see cref="MethodTag"/> and <see cref="ToolNameTag"/> are listed but
    /// rewritten first, since both can carry caller text.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedMcpTags = new HashSet<string>(StringComparer.Ordinal)
    {
        MethodTag, ToolNameTag, "gen_ai.operation.name", "error.type", "rpc.response.status_code",
        "rpc.system", "mcp.protocol.version", "jsonrpc.protocol.version", "network.transport",
        "network.protocol.name", "network.protocol.version", "server.address", "server.port",
    };

    /// <summary>
    /// Every method name the SDK defines, read from its own constants so this follows an SDK upgrade.
    /// A request naming any other method still produces a span — named, and tagged, with the caller's
    /// text — so an unknown one is rewritten to <see cref="Unrecognised"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownMethods = typeof(ModelContextProtocol.Protocol.RequestMethods)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Concat(typeof(ModelContextProtocol.Protocol.NotificationMethods)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);

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

        // The method, from its tag when present, else from the span name's first word (the SDK names a
        // span "<method>" or "<method> <target>"), and kept only when the SDK defines it.
        var rawMethod = activity.GetTagItem(MethodTag) as string ?? FirstWord(activity.DisplayName);
        var method = KnownMethods.Contains(rawMethod) ? rawMethod : Unrecognised;
        if (activity.GetTagItem(MethodTag) is not null)
        {
            activity.SetTag(MethodTag, method);
        }

        string? tool = null;
        if (activity.GetTagItem(ToolNameTag) is string toolName)
        {
            tool = knownTools.IsRegistered(toolName) ? toolName : Unrecognised;
            activity.SetTag(ToolNameTag, tool);
        }

        foreach (var key in activity.TagObjects.Select(t => t.Key).ToList())
        {
            if (!AllowedMcpTags.Contains(key))
            {
                activity.SetTag(key, null);
            }
        }

        // Rebuilt from the cleaned values, never patched: the original carries whatever target the
        // caller named — a tool, a prompt, a resource. Only a registered tool name is ever appended.
        activity.DisplayName = tool is null ? method : method + " " + tool;
    }

    private static string FirstWord(string value)
    {
        var space = value.IndexOf(' ');
        return space < 0 ? value : value[..space];
    }
}
