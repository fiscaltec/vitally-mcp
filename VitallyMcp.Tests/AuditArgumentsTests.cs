using System.Text.Json;
using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

/// <summary>
/// The acceptance criterion for the audit trail (#147) is that a record shows <i>this user</i> called
/// <i>this tool</i> and touched data for <i>these customers</i>. Tool arguments are half of that: they
/// are the scope the caller asked for. So these tests are about a deliberate policy reversal
/// (2026-09-17) rather than incidental formatting — the previous rule kept personal data out of
/// telemetry entirely, and left the trail unable to answer the question it exists for.
/// </summary>
public class AuditArgumentsTests
{
    private static IReadOnlyDictionary<string, JsonElement> Args(params (string Name, object? Value)[] pairs) =>
        Args(pairs.Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)).ToArray());

    private static IReadOnlyDictionary<string, JsonElement> Args(params KeyValuePair<string, object?>[] pairs)
    {
        var json = JsonSerializer.Serialize(pairs.ToDictionary(p => p.Key, p => p.Value));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    [Fact]
    public void Format_PreservesAFreeTextSearchTermVerbatim()
    {
        // `Search_users` takes an email as its search term, and recording it is the point rather
        // than an oversight: without the term, a search that read twenty customers records that
        // *something* was searched for and names nothing.
        var result = AuditArguments.Format(Args(("query", "alice@example.com")));

        result.Rendered.Should().Contain("alice@example.com");
        result.Truncated.Should().BeFalse();
    }

    [Fact]
    public void Format_TruncatesAnOversizedValue_AndMarksTheRecord()
    {
        // The cap matches `VitallyService.Truncate(body, 1024)` deliberately, so one number governs
        // how much free text this server ever puts in a log line. The argument is shortened rather
        // than dropped: knowing a caller passed *some* 2 KB filter is worth more than a record that
        // silently omits the argument and reads as though none was given.
        var longValue = new string('a', 2000);

        var result = AuditArguments.Format(Args(("notes", longValue)));

        result.Truncated.Should().BeTrue();
        result.Rendered.Should().Contain("notes", "the argument is kept, only its value is shortened");
        result.Rendered.Should().NotContain(longValue, "the whole value must not survive the cap");
        result.Rendered.Should().Contain(new string('a', 100), "the start of the value is kept");
    }

    [Fact]
    public void Format_KeepsTheRenderedRecordWithinTheSetBudget()
    {
        // Ten arguments, each comfortably under the per-value cap, together far over the set budget.
        // The per-value cap alone does not bound a record — a caller passing many medium-sized
        // filters would otherwise write an unbounded line.
        var args = Enumerable.Range(0, 10)
            .Select(i => new KeyValuePair<string, object?>($"filter{i}", new string('b', 1000)))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Truncated.Should().BeTrue();
        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096, "the set budget bounds the record");
    }

    [Fact]
    public void Format_NeverTruncatesAScopingIdentifier_EvenWhenTheBudgetIsExhausted()
    {
        // Enough argument *names* to exhaust the budget on structure alone, so every value is
        // competing for nothing. The scoping id is what names the customer, so it is the last thing
        // that may be cut — a record that proves a call happened but cannot say who it touched
        // fails the acceptance criterion outright.
        var args = Enumerable.Range(0, 400)
            .Select(i => new KeyValuePair<string, object?>($"filter{i}", new string('c', 50)))
            .Append(new KeyValuePair<string, object?>("organizationId", "org-9f3c2b1a"))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Rendered.Should().Contain("org-9f3c2b1a",
            "the scoping identifier survives even when every other value has been squeezed out");
    }

    [Fact]
    public void Format_DoesNotLetAnIdentifierNamedArgumentBypassTheBudget()
    {
        // The scoping exemption is name-based, so without a length condition it is an open hole:
        // call an argument `somethingId`, pass 50 KB, and the field added to guarantee attribution
        // becomes the one that lets an unbounded value into the audit record.
        var oversized = new string('d', 50_000);

        var result = AuditArguments.Format(Args(("sneakyId", oversized)));

        result.Truncated.Should().BeTrue();
        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096,
            "an identifier-shaped name does not exempt free text from the budget");
    }

    [Fact]
    public void Format_KeepsTheRecordWithinBudget_EvenWhenEveryCharacterEscapes()
    {
        // The allowance was measured on the DECODED value while the budget is spent on the RENDERED
        // record. A quote costs one character to hold and two to write, so a set that fits its
        // allowance can still serialise past the cap — and a `jsonBody` argument is mostly quotes.
        var args = Enumerable.Range(0, 10)
            .Select(i => new KeyValuePair<string, object?>($"filter{i}", new string('"', 1000)))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096,
            "the cap governs what is written, not what was measured");
    }

    [Fact]
    public void Format_BoundsTheRecord_EvenWhenEveryArgumentClaimsTheScopingExemption()
    {
        // The exemption writes scoping identifiers in full and charges them against the budget —
        // which bounds the FREE TEXT but not the record, because an exempt value is still written
        // after the budget has gone negative. Each one is individually capped at 256, so a caller
        // passing many of them can still blow the documented set cap.
        var args = Enumerable.Range(0, 100)
            .Select(i => new KeyValuePair<string, object?>($"filter{i}Id", new string('e', 250)))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        // Bounded against the ~26 KB this produced before the fix. The bound is on the VALUES: the
        // documented contract is that argument names are never dropped, so a call naming a hundred
        // arguments still pays for a hundred names.
        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096,
            "scoping identifiers take priority from the budget rather than bypassing it");
    }
}
