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
}
