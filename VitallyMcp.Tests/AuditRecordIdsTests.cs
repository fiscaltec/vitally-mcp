using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

/// <summary>
/// The half of #147's acceptance criterion that the upstream path cannot supply. A get-by-id call
/// names its customer in the URL, and so do scoped lists like <c>organizations/{id}/users</c> — but
/// an <b>unscoped</b> list or search, which is most reads, carries the identities only in the
/// response body. Without these ids, <c>List_organizations(limit=100)</c> records that a hundred
/// customers were read and names none of them.
/// </summary>
public class AuditRecordIdsTests
{
    [Fact]
    public void Extract_NamesTheCustomersInAStandardListEnvelope()
    {
        var raw = """{"results":[{"id":"org-1","name":"A"},{"id":"org-2","name":"B"}],"next":"cursor-9"}""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("org-1", "org-2");
        result.RecordsFetched.Should().Be(2);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_CapsTheIdList_ButStillReportsHowManyWereRead()
    {
        // The bounded auto-pager can fetch ten pages of a hundred, so an uncapped list is ~1000 ids
        // in one record. Capping without reporting the count would under-report a bulk read:
        // "read 640 organisations, first 100 listed" is honest, "read 100" is not.
        var records = string.Join(",", Enumerable.Range(0, 640).Select(i => $$"""{"id":"org-{{i}}"}"""));
        var raw = $$"""{"results":[{{records}}]}""";

        var result = AuditRecordIds.Extract(raw);

        result.IdsRecorded.Should().Be(100, "the id list is capped");
        result.RecordsFetched.Should().Be(640, "the magnitude of the read is reported regardless");
        result.Ids.Should().StartWith(["org-0"]).And.EndWith(["org-99"]);
    }

    [Fact]
    public void Extract_ReadsABareArray_AsTheRawPassThroughEndpointsReturn()
    {
        // `customFields` returns a bare array rather than the `{results, next}` envelope. Covering
        // only the standard shape would leave the raw pass-through tools recording nothing.
        var raw = """[{"id":"trait-1"},{"id":"trait-2"}]""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("trait-1", "trait-2");
        result.RecordsFetched.Should().Be(2);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_ReadsTheSurveysDataEnvelope()
    {
        // `surveys/:id/responses` uses `{data}` rather than `{results, next}`.
        var raw = """{"data":[{"id":"resp-1"},{"id":"resp-2"}],"next":null}""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("resp-1", "resp-2");
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_NamesTheCustomerOnAGetByIdResponse()
    {
        // A single record comes back as a bare object. The upstream path names this customer too, so
        // the id is corroboration rather than the only evidence — but reporting "ids unavailable"
        // here would be a false gap, and a false gap is worse than a real one: it sends whoever is
        // reading the trail looking for a limitation that does not exist.
        var raw = """{"id":"org-42","name":"Acme","traits":{"mrr":1000}}""";

        var result = AuditRecordIds.Extract(raw);

        result.Ids.Should().Equal("org-42");
        result.RecordsFetched.Should().Be(1);
        result.IdsAvailable.Should().BeTrue();
    }

    [Fact]
    public void Extract_ReportsAnExplicitGap_WhenTheShapeYieldsNoIds()
    {
        // "Ids unavailable for this path" is a deliberate outcome. A record that silently omits them
        // looks complete while naming nobody, which is the one failure mode the acceptance criterion
        // cannot tolerate.
        var raw = """{"deleted":true}""";

        var result = AuditRecordIds.Extract(raw);

        result.IdsAvailable.Should().BeFalse();
        result.Ids.Should().BeEmpty();
    }

    [Fact]
    public void Extract_DoesNotThrow_OnAResponseThatIsNotJson()
    {
        // An audit component must never be the reason a tool call fails. Vitally can return a
        // non-JSON body (a gateway error page, an empty 204), and `JsonDocument.Parse` throws on
        // those — so without this the trail would turn an upstream hiccup into a failed call.
        var act = () => AuditRecordIds.Extract("<html>502 Bad Gateway</html>");

        act.Should().NotThrow();
        AuditRecordIds.Extract("<html>502 Bad Gateway</html>").IdsAvailable.Should().BeFalse();
    }
}
