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

    /// <summary>
    /// Longest argument name a record will carry, in rendered characters.
    /// </summary>
    /// <remarks>
    /// Names are caller-controlled, so leaving them unbounded made the set cap unenforceable — a
    /// 50 KB name writes a 50 KB log line whatever the value budget says. They are <b>truncated</b>
    /// rather than dropped: an argument that vanished would read as one the caller never sent, but
    /// protecting against that misreading at the price of an unbounded write is the worse trade.
    /// Real MCP parameter names are tens of characters.
    /// </remarks>
    internal const int MaxNameChars = 128;

    /// <summary>Longest value that may still claim scoping-identifier priority.</summary>
    internal const int MaxScopingIdentifierChars = 256;

    /// <summary>Property naming how many arguments did not fit.</summary>
    private const string OmittedPropertyName = "__omittedArguments";

    /// <summary>Space held back so the omission count can always be written.</summary>
    private const int OmissionReserve = 32;

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

        var namesTruncated = false;
        var entries = arguments
            .Select(a =>
            {
                var text = AsText(a.Value);
                var isString = a.Value.ValueKind == JsonValueKind.String;
                var key = a.Key;
                if (EscapedLength(key) > MaxNameChars)
                {
                    key = TakeWithinEscapedBudget(key, MaxNameChars - TruncationMarker.Length)
                        + TruncationMarker;
                    namesTruncated = true;
                }

                return new Entry(
                    Key: key,
                    Text: text,
                    Value: a.Value,
                    Scoping: IsScopingIdentifier(key, text),
                    KeyCost: EscapedLength(key) + 6,
                    // What this value will actually COST once written. A string is escaped on the way
                    // out — a quote costs one character to hold and two to write — while any other
                    // element is emitted as its own raw JSON.
                    Cost: isString ? EscapedLength(text) : text.Length);
            })
            .ToList();

        if (entries.Count == 0)
        {
            return new AuditedArguments("{}", Truncated: false);
        }

        // Shortening names can make two DISTINCT arguments share one key — anything past the cap
        // collapses to the same prefix — and a record with duplicate keys is read arbitrarily by a
        // parser, so the reader cannot tell which value belonged to which argument. Same
        // evidence-ambiguity failure as the omission property, reached through the fix for a
        // different problem. Input keys are unique by construction, so only truncation causes this.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            if (seen.Add(entries[i].Key))
            {
                continue;
            }

            var suffix = 2;
            string candidate;
            do
            {
                candidate = entries[i].Key + "~" + suffix++;
            }
            while (!seen.Add(candidate));

            entries[i] = entries[i] with { Key = candidate, KeyCost = EscapedLength(candidate) + 6 };
            namesTruncated = true;
        }

        // A hard bound, which it was not before. Capping each name still let a caller choose how many
        // names there were, so the line grew with the argument count — a cost and availability problem,
        // not just an untidy one. Arguments that do not fit are OMITTED and counted, which is why the
        // omission property's own cost is reserved up front.
        var budget = MaxTotalChars - 2 - OmissionReserve;
        var allowances = new int[entries.Count];
        var included = new bool[entries.Count];
        var omitted = 0;

        // Scoping identifiers are allocated first and only ever in FULL. A half-written customer id
        // is worse than a stated gap: it still looks like an id and resolves to nothing, so a reader
        // cannot tell it from a real one. Complete, or counted as omitted.
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Scoping)
            {
                continue;
            }

            var need = entries[i].KeyCost + entries[i].Cost;
            if (need <= budget)
            {
                included[i] = true;
                allowances[i] = entries[i].Cost;
                budget -= need;
            }
            else
            {
                omitted++;
            }
        }

        // Free text shares whatever survives, each paying for its own name first.
        var freeRemaining = entries.Count(e => !e.Scoping);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Scoping)
            {
                continue;
            }

            freeRemaining--;
            // Omit only when even the NAME will not fit. #147 says overflow truncates the value and
            // marks the record rather than dropping the argument, because an argument that vanished
            // reads as one the caller never sent — so a key with an empty value is strictly better
            // evidence than an omission, and costs nothing beyond the key itself.
            if (budget < entries[i].KeyCost)
            {
                omitted++;
                continue;
            }

            budget -= entries[i].KeyCost;
            var share = freeRemaining > 0 ? Math.Max(0, budget / (freeRemaining + 1)) : budget;
            included[i] = true;
            allowances[i] = Math.Min(MaxValueChars, Math.Min(share, entries[i].Cost));
            budget -= allowances[i];
        }

        var truncated = namesTruncated || omitted > 0;
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            for (var i = 0; i < entries.Count; i++)
            {
                if (!included[i])
                {
                    continue;
                }

                var entry = entries[i];
                if (entry.Cost <= allowances[i])
                {
                    // Write the original element so a number stays a number and an object stays an
                    // object — the record is evidence, and re-typing it loses fidelity for nothing.
                    writer.WritePropertyName(entry.Key);
                    entry.Value.WriteTo(writer);
                    continue;
                }

                // Once the allowance cannot even hold the marker, write an empty value rather than a
                // bare one. The marker is otherwise unbudgeted — this exact oversight put the record
                // 645 characters over the cap when the allocator was rewritten, having already done
                // it once before at 266.
                if (allowances[i] < TruncationMarker.Length)
                {
                    writer.WriteString(entry.Key, string.Empty);
                    truncated = true;
                    continue;
                }

                // Shortened, never dropped: knowing a caller passed *some* oversized filter beats a
                // record that reads as though none was given. The cut is made against the ESCAPED
                // length, because that is what the allowance buys, and the marker counts against it.
                var kept = TakeWithinEscapedBudget(entry.Text, allowances[i] - TruncationMarker.Length);
                writer.WriteString(entry.Key, kept + TruncationMarker);
                truncated = true;
            }

            if (omitted > 0)
            {
                // Stated, not silent. A record that simply stopped would read as a call made with
                // fewer arguments than it was.
                //
                // The name is disambiguated against the caller's own, because argument names are
                // caller-controlled: a client sending an argument called `__omittedArguments` would
                // otherwise produce a record with DUPLICATE keys, and a parser picks one arbitrarily.
                // Audit evidence a caller can make ambiguous is not evidence.
                var name = OmittedPropertyName;
                var written = entries.Where((_, i) => included[i]).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
                for (var attempt = 1; written.Contains(name); attempt++)
                {
                    name = $"{OmittedPropertyName}~{attempt}";
                }

                writer.WriteNumber(name, omitted);
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
    private static int EscapedLength(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        // Bound the WORK as well as the output. Encoding a caller's whole value just to learn it is
        // too big made the cost proportional to the input on every call — a 10 MB jsonBody meant
        // 10 MB of encoding for a record that was always going to be 4 KB, and concurrent calls
        // amplify that. Escaping never shrinks a string, so once the raw length is past the largest
        // allowance any value could receive, the raw length is already a sufficient answer: it is a
        // lower bound, and every comparison this feeds is "is this over the allowance?".
        if (text.Length > MaxValueChars)
        {
            return text.Length;
        }

        return JsonEncodedText.Encode(text, Encoder).Value.Length;
    }

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

    /// <summary>One argument, as the allocator needs to see it.</summary>
    private readonly record struct Entry(
        string Key,
        string Text,
        JsonElement Value,
        bool Scoping,
        int KeyCost,
        int Cost);

    private static string AsText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
}
