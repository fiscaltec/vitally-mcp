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

    [Fact]
    public void Format_AccountsForEscapingInArgumentNamesAsWellAsValues()
    {
        // Argument NAMES come from the MCP client too, and `WritePropertyName` escapes them exactly
        // as values are escaped. The budget was taught to count escaped value length and left
        // counting raw key length — so the values were handed a share computed against names that
        // cost twice what the arithmetic allowed. Same defect as the value one, one field over.
        //
        // Note the invariant being tested is about the VALUES shrinking to fit: a single enormous
        // name cannot be bounded at all, because names are never dropped (see the class remarks).
        var quotedKey = new string('"', 10);
        var args = Enumerable.Range(0, 100)
            .Select(i => new KeyValuePair<string, object?>(quotedKey + i, new string('v', 500)))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096,
            "escaped names must be budgeted at what they cost to write, so the values shrink to fit");
    }

    [Fact]
    public void Format_BoundsTheRecord_EvenWhenTheArgumentNameItselfIsEnormous()
    {
        // Argument NAMES are caller-controlled, so "names are never dropped" — the rule this file
        // documented until now — made the cap unenforceable: a 50 KB name writes a 50 KB log line
        // whatever the value budget says. That protected against a record misreading as though the
        // caller sent no argument, at the price of an unbounded write, which is the worse trade.
        //
        // The name is TRUNCATED rather than dropped, so the argument still appears and the record is
        // still bounded.
        var enormousName = new string('n', 50_000);

        var result = AuditArguments.Format(Args((enormousName, "value")));

        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096,
            "a caller-controlled name cannot be an unbounded path around the cap");
        result.Truncated.Should().BeTrue("and the record says something was shortened");
        result.Rendered.Should().Contain("nnnn", "the name is shortened, not dropped");
    }

    [Fact]
    public void Format_IsAHardBound_HoweverManyArgumentsAreSupplied()
    {
        // The floor left the cap advertised but unenforceable: each NAME is capped, but a caller
        // controls how many names there are, so overhead grew without limit and the floor still
        // reserved value space on top. A log line whose size a caller chooses is a cost and
        // availability problem, not only an untidy one.
        var args = Enumerable.Range(0, 2000)
            .Select(i => new KeyValuePair<string, object?>($"filter{i}", new string('f', 100)))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096, "the cap is a bound, not a target");
        result.Truncated.Should().BeTrue();
        result.Rendered.Should().Contain("omitted", "and the record says arguments were left out");
    }

    [Fact]
    public void Format_NeverWritesAPartialScopingIdentifier()
    {
        // The two rules finally reconciled. A scoping identifier is either recorded COMPLETE or
        // counted as omitted — never shortened. A half-written customer id is worse than a stated
        // gap: it looks like a customer and resolves to nothing, so a reader cannot tell a truncated
        // id from a real one.
        var args = Enumerable.Range(0, 60)
            .Select(i => new KeyValuePair<string, object?>($"customer{i}Id", new string('i', 250)))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096);
        result.Rendered.Should().NotContain("i...", "a scoping id is complete or absent, never cut");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(90210)]
    public void Format_NeverExceedsTheCap_AcrossRandomisedArgumentShapes(int seed)
    {
        // Every previous bound failure here was found by someone constructing one specific shape:
        // escaped values, then the scoping exemption, then the unbudgeted marker, then the negative
        // budget, then escaped names, then unbounded names, then argument count. Seven shapes, seven
        // separate rounds. A per-bug example test cannot catch the eighth, so this fuzzes the shapes
        // instead — seeded, so a failure is reproducible.
        var random = new Random(seed);
        // Deliberately includes the characters that escape to more than themselves — a quote and a
        // backslash cost two, a newline and a tab cost two, and the non-ASCII ones exercise the
        // relaxed encoder and the surrogate-safe cut.
        var alphabet = new[] { 'a', '"', '\\', '\n', '\t', 'é', '本', ' ' };
        string Noise(int length) =>
            new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());

        var args = Enumerable.Range(0, random.Next(1, 300))
            .Select(i =>
            {
                var name = random.Next(4) == 0
                    ? Noise(random.Next(1, 400)) + i
                    : (random.Next(3) == 0 ? $"thing{i}Id" : $"filter{i}");
                object? value = random.Next(5) switch
                {
                    0 => random.Next(),
                    1 => Noise(random.Next(0, 3000)),
                    2 => new string('x', random.Next(0, 2000)),
                    3 => null,
                    _ => Noise(random.Next(0, 300)),
                };
                return new KeyValuePair<string, object?>(name, value);
            })
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Rendered.Length.Should().BeLessThanOrEqualTo(4096,
            $"seed {seed} produced a record over the cap");
        var act = () => JsonDocument.Parse(result.Rendered);
        act.Should().NotThrow($"seed {seed} produced malformed JSON");
    }

    /// <summary>
    /// U+2028 LINE SEPARATOR, built from its code point. The backslash-u escape cannot appear
    /// in this file at all — not even inside a comment — because C# resolves unicode escapes
    /// before lexing and treats that character as a line terminator. A neat illustration of why
    /// it is dangerous in a line-oriented format.
    /// </summary>
    private static readonly string LineSeparator = ((char)0x2028).ToString();

    [Fact]
    public void Format_NeutralisesUnicodeLineSeparatorsInValues()
    {
        // U+2028 and U+2029 are category Zl/Zp, NOT Cc — `char.IsControl` is false for them and the
        // relaxed encoder passes them through untouched. Plenty of log readers still break lines on
        // them, so a caller-controlled search term carrying one forges an audit record exactly as a
        // newline would. The round that "fixed" log injection here closed only the ASCII half.
        var result = AuditArguments.Format(
            Args(("query", "evil" + LineSeparator + "Vitally audit: forged")));

        // Sanity first: the assertions below are meaningless if the value never made it in.
        result.Rendered.Should().Contain("evil").And.Contain("forged");

        result.Rendered.Should().NotContain(LineSeparator, "no raw line separator reaches the log line");
        result.Rendered.Should().Contain("u2028", "escaped rather than dropped, so the attempt stays visible");
    }

    [Fact]
    public void Format_DoesNotCollideWithACallerSuppliedOmissionPropertyName()
    {
        // The omission count is written as a synthetic property, and argument names are
        // caller-controlled — so a client can send an argument called `__omittedArguments` and, if
        // anything is then omitted, the record carries DUPLICATE keys. Parsers pick one arbitrarily,
        // so the reader may see the caller's value or the real count and cannot tell which. Audit
        // evidence a caller can make ambiguous is not evidence.
        var args = Enumerable.Range(0, 500)
            .Select(i => new KeyValuePair<string, object?>($"filter{i}", new string('g', 200)))
            .Prepend(new KeyValuePair<string, object?>("__omittedArguments", "spoofed"))
            .ToArray();

        var result = AuditArguments.Format(Args(args));

        result.Truncated.Should().BeTrue("this set cannot fit, so something is omitted");
        using var parsed = JsonDocument.Parse(result.Rendered);
        var names = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().OnlyHaveUniqueItems("a record with duplicate keys cannot be read unambiguously");
    }

    [Fact]
    public void Format_DisambiguatesArgumentNamesThatTruncateToTheSamePrefix()
    {
        // Name truncation was itself a way to make the record ambiguous: two DISTINCT names sharing
        // their first 125 rendered characters both became the same prefix plus the marker, so the
        // record carried duplicate keys and a parser returned whichever it liked. Same class as the
        // omission-property collision, reached through the fix for a different problem.
        var shared = new string('p', 300);
        var args = Args(
            (shared + "alpha", "first"),
            (shared + "beta", "second"));

        var result = AuditArguments.Format(args);

        using var parsed = JsonDocument.Parse(result.Rendered);
        var names = parsed.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().HaveCount(2).And.OnlyHaveUniqueItems(
            "two distinct arguments must stay distinguishable after their names are shortened");
    }
}
