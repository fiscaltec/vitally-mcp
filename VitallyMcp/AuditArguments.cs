using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VitallyMcp;

/// <summary>The rendered arguments of one tool call, and whether anything had to be shortened.</summary>
public readonly record struct AuditedArguments(string Rendered, bool Truncated);

/// <summary>
/// Renders a tool call's arguments for the audit record.
/// </summary>
/// <remarks>
/// Arguments are recorded <b>in full</b>, free-text search terms included, under the policy agreed on
/// 2026-09-17: without them the trail cannot say which customer was accessed on an unscoped list or
/// search, where the identities exist only in the response body. Response bodies remain excluded —
/// that boundary is unchanged.
/// </remarks>
public static class AuditArguments
{
    /// <summary>
    /// Per-value cap, counted in <i>rendered</i> characters. Matches <c>VitallyService</c>'s own
    /// <c>Truncate(body, 1024)</c> deliberately, so one number governs how much free text this server
    /// puts into a log line.
    /// </summary>
    internal const int MaxValueChars = 1024;

    /// <summary>
    /// Budget for the whole rendered set. Argument <i>names</i> are never dropped, so a call with a
    /// great many arguments can still exceed this — the budget bounds the free text, which is what
    /// actually grows without limit.
    /// </summary>
    internal const int MaxTotalChars = 4096;

    /// <summary>Longest value that may still claim the scoping-identifier exemption.</summary>
    internal const int MaxScopingIdentifierChars = 256;

    // ASCII, and deliberately so. The writer escapes non-ASCII by default, so a one-character "…"
    // renders as the six characters … — which silently overran the set budget by 5 per truncated
    // value when this was first written. A marker that costs what it appears to cost keeps the
    // arithmetic below honest.
    private const string TruncationMarker = "...";

    // Relaxed escaping so a non-ASCII argument value (an accented name, a unicode search term) is not
    // inflated six-fold. This output is a log record, never HTML, so the relaxed encoder is correct.
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = Encoder };

    /// <summary>
    /// Renders the arguments as a bounded JSON object, shortening values as needed to stay inside
    /// <see cref="MaxTotalChars"/>.
    /// </summary>
    /// <remarks>
    /// Takes a key/value sequence rather than a dictionary interface deliberately: the MCP SDK hands
    /// the call's arguments over as <c>IDictionary</c>, which does <b>not</b> implement
    /// <c>IReadOnlyDictionary</c>, so a narrower parameter would compile in the tests and fail at the
    /// one call site that matters.
    /// </remarks>
    public static AuditedArguments Format(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (arguments is null)
        {
            return new AuditedArguments("{}", Truncated: false);
        }

        var entries = arguments
            .Select(a =>
            {
                var text = AsText(a.Value);
                var isString = a.Value.ValueKind == JsonValueKind.String;
                return (
                    a.Key,
                    Text: text,
                    a.Value,
                    Scoping: IsScopingIdentifier(a.Key, text),
                    // What this value will actually COST once written. A string is escaped on the way
                    // out — a quote costs one character to hold and two to write — while any other
                    // element is emitted as its own raw JSON. Budgeting on the decoded length let a
                    // set of quote-heavy values (a `jsonBody` argument is mostly quotes) render to
                    // roughly twice the cap.
                    Cost: isString ? EscapedLength(text) : text.Length);
            })
            .ToList();

        if (entries.Count == 0)
        {
            return new AuditedArguments("{}", Truncated: false);
        }

        // Reserve the structural cost up front — quotes, colons, commas and the names themselves —
        // so the value budget is what is actually left rather than an optimistic figure the entries
        // then overrun one by one.
        var overhead = entries.Sum(e => e.Key.Length + 6);

        // Scoping identifiers are written in full and charged against the budget rather than
        // competing for it, so the free text is what shrinks. They are what names the customer, and
        // a record that proves a call happened without saying who it touched fails the whole point.
        var scopingCost = entries.Where(e => e.Scoping).Sum(e => e.Cost);
        var valueBudget = MaxTotalChars - 2 - overhead - scopingCost;
        var remaining = entries.Count(e => !e.Scoping);
        var truncated = false;

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var entry in entries)
            {
                if (entry.Scoping)
                {
                    writer.WritePropertyName(entry.Key);
                    entry.Value.WriteTo(writer);
                    continue;
                }

                // An equal share of what is left, recomputed each time, so a short value hands its
                // unused share to the values that follow rather than wasting it.
                var share = remaining > 0 ? Math.Max(0, valueBudget / remaining) : 0;
                var allowed = Math.Min(MaxValueChars, share);
                remaining--;

                if (entry.Cost <= allowed)
                {
                    // Write the original element so a number stays a number and an object stays an
                    // object — the record is evidence, and re-typing it loses fidelity for nothing.
                    writer.WritePropertyName(entry.Key);
                    entry.Value.WriteTo(writer);
                    valueBudget -= entry.Cost;
                    continue;
                }

                // Shorten the value, never drop the argument: knowing a caller passed *some*
                // oversized filter beats a record that reads as though none was given. The marker
                // counts against the allowance rather than being added on top of it, and the cut is
                // made against the ESCAPED length, because that is what the allowance buys.
                var kept = TakeWithinEscapedBudget(entry.Text, allowed - TruncationMarker.Length);
                writer.WriteString(entry.Key, kept + TruncationMarker);
                valueBudget -= allowed;
                truncated = true;
            }

            writer.WriteEndObject();
        }

        return new AuditedArguments(Encoding.UTF8.GetString(buffer.ToArray()), truncated);
    }

    /// <summary>
    /// Whether an argument names a record rather than filtering on free text — <c>organizationId</c>,
    /// <c>accountIds</c>, <c>externalId</c>, a bare <c>id</c>.
    /// </summary>
    /// <remarks>
    /// The length condition is not belt-and-braces. Without it the exemption is an unbounded hole:
    /// any caller could name an argument <c>xId</c> and write a megabyte into the audit record,
    /// turning the field meant to guarantee attribution into the one that lets the budget be
    /// bypassed. Real identifiers are tens of characters, so anything longer is free text wearing
    /// an identifier's name and is budgeted as such.
    /// </remarks>
    internal static bool IsScopingIdentifier(string name, string text) =>
        text.Length <= MaxScopingIdentifierChars
        && (name.Equals("id", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase));

    /// <summary>How many characters <paramref name="text"/> occupies once written as a JSON string.</summary>
    private static int EscapedLength(string text) =>
        text.Length == 0 ? 0 : JsonEncodedText.Encode(text, Encoder).Value.Length;

    /// <summary>
    /// The longest prefix of <paramref name="text"/> that still fits <paramref name="budget"/> once
    /// escaped. Found by bisection rather than arithmetic because the cost per character is not
    /// uniform — a quote costs two, a control character six, most characters one.
    /// </summary>
    private static string TakeWithinEscapedBudget(string text, int budget)
    {
        if (budget <= 0)
        {
            return string.Empty;
        }

        var low = 0;
        var high = Math.Min(text.Length, budget);
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (EscapedLength(text[..SafeCut(text, mid)]) <= budget)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return text[..SafeCut(text, low)];
    }

    /// <summary>
    /// Pulls a cut back off a surrogate pair. Slicing between the two halves of an astral character
    /// leaves a lone surrogate, which is not valid text to encode.
    /// </summary>
    private static int SafeCut(string text, int index) =>
        index > 0 && char.IsHighSurrogate(text[index - 1]) ? index - 1 : index;

    private static string AsText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
