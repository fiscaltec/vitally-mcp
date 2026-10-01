using System.Diagnostics;
using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

/// <summary>
/// The gate on exporting dependency telemetry: a search term must never leave this process inside a
/// span attribute.
/// </summary>
/// <remarks>
/// <c>Search_users</c> passes its term as <c>?query=&lt;value&gt;</c> and the tool's own description
/// says the term is an email or externalId, so this is the concrete path by which a customer's email
/// could reach <c>AppDependencies</c> — a different table from the audit records, with its own
/// retention, and one <see cref="AuditLogger"/>'s <c>ResourcePath</c> stripping does not touch.
/// </remarks>
// Serialised with the integration tests because the sanitiser tests create spans on the MCP SDK's
// source NAME, and two tests there depend on nothing else listening to it: the exporter test's
// post-dispose guard, and the composed PII test's catch-all listener. Running in parallel, these
// would flake both.
[Collection(IntegrationTestCollection.Name)]
public class TelemetryRedactionTests
{
    [Theory]
    [InlineData("https://rest.vitally-eu.io/resources/users/search?query=alice@example.com",
                "https://rest.vitally-eu.io/resources/users/search?*")]
    [InlineData("https://rest.vitally-eu.io/resources/organizations?limit=100&from=cursor",
                "https://rest.vitally-eu.io/resources/organizations?*")]
    [InlineData("https://rest.vitally-eu.io/resources/accounts/acc-1",
                "https://rest.vitally-eu.io/resources/accounts/acc-1")]
    public void Redact_RemovesTheQuery_AndLeavesThePath(string url, string expected) =>
        QueryStringRedactingProcessor.Redact(url).Should().Be(expected,
            "the path carries the record id, which the trail records deliberately; the query carries "
            + "the search term, which it must not");

    [Fact]
    public void Redact_LeavesAnAlreadyRedactedUrlAlone()
    {
        // The runtime redacts url.full itself on .NET 9+, so most spans arrive in this shape. Without
        // this the processor would append its own marker to the runtime's and produce "?*​*".
        const string alreadyRedacted = "https://rest.vitally-eu.io/resources/users/search?*";

        QueryStringRedactingProcessor.Redact(alreadyRedacted).Should().Be(alreadyRedacted);
    }

    [Fact]
    public void OnEnd_StripsTheQueryFromASpanAttribute()
    {
        using var source = new ActivitySource("VitallyMcp.Tests.Redaction");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "VitallyMcp.Tests.Redaction",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("GET")!;
        activity.SetTag("url.full", "https://rest.vitally-eu.io/resources/users/search?query=bob@example.com");

        new QueryStringRedactingProcessor().OnEnd(activity);

        activity.GetTagItem("url.full").Should().Be("https://rest.vitally-eu.io/resources/users/search?*");
    }

    private const string SanitiseSource = "VitallyMcp.Tests.Sanitise";
    private static readonly KnownToolNames Known = new(["List_organizations"]);

    private static (ActivitySource Source, ActivityListener Listener) StartSource(string name)
    {
        var source = new ActivitySource(name);
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        return (source, listener);
    }

    [Fact]
    public void Sanitiser_ClearsTheStatusDescription_ButKeepsTheErrorStatus()
    {
        // The MCP SDK copies a failed tool call's whole error text into the span's StatusDescription,
        // and that text carries the caller's arguments ("got 'alice@example.com'") and Vitally's
        // response body. The status code says everything an operator needs; the text is customer data.
        var (source, listener) = StartSource(SanitiseSource);
        using var _source = source;
        using var _listener = listener;
        using var activity = source.StartActivity("tools/call List_organizations")!;
        activity.SetStatus(ActivityStatusCode.Error, "Upstream API returned 400. Body: alice@example.com");

        new SpanSanitisingProcessor(Known).OnEnd(activity);

        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Sanitiser_ReplacesAnUnregisteredToolName_InTheTagAndTheSpanName()
    {
        // A tools/call naming a tool that does not exist still produces a span, and the SDK puts the
        // invented name in gen_ai.tool.name and in the span name — caller-controlled text, which the
        // audit breadcrumb and the failure log already refuse to repeat.
        var (source, listener) = StartSource(TelemetrySources.McpActivitySource);
        using var _source = source;
        using var _listener = listener;
        using var activity = source.StartActivity("tools/call Alice_Smith_customer")!;
        activity.SetTag("gen_ai.tool.name", "Alice_Smith_customer");

        new SpanSanitisingProcessor(Known).OnEnd(activity);

        activity.GetTagItem("gen_ai.tool.name").Should().Be("unrecognised");
        activity.DisplayName.Should().Be("tools/call unrecognised");
    }

    [Fact]
    public void Sanitiser_LeavesARegisteredToolName_Alone()
    {
        var (source, listener) = StartSource(TelemetrySources.McpActivitySource);
        using var _source = source;
        using var _listener = listener;
        using var activity = source.StartActivity("tools/call List_organizations")!;
        activity.SetTag("gen_ai.tool.name", "List_organizations");

        new SpanSanitisingProcessor(Known).OnEnd(activity);

        activity.GetTagItem("gen_ai.tool.name").Should().Be("List_organizations");
        activity.DisplayName.Should().Be("tools/call List_organizations");
    }

    [Fact]
    public void Sanitiser_DropsTheCallerChosenRequestId()
    {
        // The JSON-RPC id is whatever the client sent, and it is on every MCP span.
        var (source, listener) = StartSource(TelemetrySources.McpActivitySource);
        using var _source = source;
        using var _listener = listener;
        using var activity = source.StartActivity("tools/call List_organizations")!;
        activity.SetTag("jsonrpc.request.id", "alice@example.com");

        new SpanSanitisingProcessor(Known).OnEnd(activity);

        activity.GetTagItem("jsonrpc.request.id").Should().BeNull();
    }
}
