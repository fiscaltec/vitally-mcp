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
    /// Per-value cap. Matches <c>VitallyService</c>'s own <c>Truncate(body, 1024)</c> deliberately, so
    /// one number governs how much free text this server puts into a log line.
    /// </summary>
    internal const int MaxValueChars = 1024;

    // ASCII, and deliberately so. The writer escapes non-ASCII by default, so a one-character "…"
    // renders as the six characters … — which silently overran the set budget by 5 per truncated
    // value when this was first written. A marker that costs what it appears to cost keeps the
    // arithmetic below honest.
    private const string TruncationMarker = "...";

    // Relaxed escaping for the same reason: the budget is counted in characters, and default escaping
    // would inflate any non-ASCII argument value (an accented name, a unicode search term) well past
    // it. This output is a log record, never HTML, so the relaxed encoder is the right one.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Budget for the whole rendered set. Argument <i>names</i> are never dropped, so a call with a
    /// great many arguments can still exceed this — the budget bounds the free text, which is what
    /// actually grows without limit.
    /// </summary>
    internal const int MaxTotalChars = 4096;

    /// <summary>Longest value that may still claim the scoping-identifier exemption.</summary>
    internal const int MaxScopingIdentifierChars = 256;

    public static AuditedArguments Format(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return new AuditedArguments("{}", Truncated: false);
        }

        var entries = arguments
            .Select(a =>
            {
                var text = AsText(a.Value);
                return (a.Key, Text: text, a.Value, Scoping: IsScopingIdentifier(a.Key, text));
            })
            .ToList();

        // Reserve the structural cost up front — quotes, colons, commas and the names themselves —
        // so the value budget is what is actually left rather than an optimistic figure the entries
        // then overrun one by one.
        var overhead = entries.Sum(e => e.Key.Length + 6);

        // Scoping identifiers are written in full and charged against the budget rather than
        // competing for it, so the free text is what shrinks. They are what names the customer, and
        // a record that proves a call happened without saying who it touched fails the whole point.
        var scopingLength = entries.Where(e => e.Scoping).Sum(e => e.Text.Length);
        var valueBudget = MaxTotalChars - 2 - overhead - scopingLength;
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

                if (entry.Text.Length <= allowed)
                {
                    // Write the original element so a number stays a number and an object stays an
                    // object — the record is evidence, and re-typing it loses fidelity for nothing.
                    writer.WritePropertyName(entry.Key);
                    entry.Value.WriteTo(writer);
                    valueBudget -= entry.Text.Length;
                    continue;
                }

                // Shorten the value, never drop the argument: knowing a caller passed *some*
                // oversized filter beats a record that reads as though none was given. The marker
                // counts against the allowance rather than being added on top of it.
                var kept = entry.Text[..Math.Max(0, allowed - TruncationMarker.Length)];
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

    private static string AsText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();

}
