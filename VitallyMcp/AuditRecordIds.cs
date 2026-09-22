using System.Text.Json;

namespace VitallyMcp;

/// <summary>
/// The record ids one upstream response returned, and how completely they were captured.
/// </summary>
/// <param name="Ids">Ids captured, up to <see cref="AuditRecordIds.MaxIds"/>.</param>
/// <param name="RecordsFetched">How many records the response actually carried.</param>
/// <param name="IdsAvailable">
/// <c>false</c> when the response shape yielded no ids — an explicit gap, so a record reads as "ids
/// unavailable for this path" rather than looking complete while naming nobody.
/// </param>
public readonly record struct AuditedRecords(
    IReadOnlyList<string> Ids,
    int RecordsFetched,
    bool IdsAvailable)
{
    public int IdsRecorded => Ids.Count;
}

/// <summary>
/// Extracts record ids from a <b>raw</b> Vitally response.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Raw</b> is the load-bearing word. <c>VitallyService.GetResourcesAsync</c> applies
/// <c>FilterJsonFields</c> <i>after</i> the upstream call, so a caller passing <c>fields=name</c>
/// receives results with no <c>id</c> at all. Extracting from the tool's result rather than the
/// upstream response would therefore name nobody on exactly the calls where a narrow projection was
/// used — a silent gap, and a plausible-looking one.
/// </para>
/// <para>
/// This is what closes the bulk-read gap: <c>List_organizations(limit=100)</c> otherwise records that
/// a hundred customers were read and names none of them, because an unscoped list carries the
/// identities only in the response body.
/// </para>
/// </remarks>
public static class AuditRecordIds
{
    /// <summary>
    /// Most ids one record will carry. The bounded auto-pager can fetch ten pages of a hundred, so an
    /// uncapped list is ~1000 ids in a single audit record; <see cref="AuditedRecords.RecordsFetched"/>
    /// keeps the true magnitude visible alongside the capped list.
    /// </summary>
    internal const int MaxIds = 100;

    private static readonly AuditedRecords Unavailable = new([], 0, IdsAvailable: false);

    public static AuditedRecords Extract(string rawJson)
    {
        // An audit component must never be the reason a tool call fails. Vitally can answer with a
        // body that is not JSON at all — a gateway error page, an empty 204 — and parsing throws on
        // those, so an unguarded parse would turn an upstream hiccup into a failed call.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return Unavailable;
        }

        using (document)
        {
            return ExtractFrom(document.RootElement);
        }
    }

    private static AuditedRecords ExtractFrom(JsonElement root)
    {
        if (!TryFindRecordArray(root, out var records))
        {
            return TryReadSingleRecord(root);
        }

        var ids = records.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var id)
                ? id.GetString()
                : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Take(MaxIds)
            .ToList();

        return new AuditedRecords(ids, records.GetArrayLength(), IdsAvailable: true);
    }

    /// <summary>
    /// Locates the array of records across the three response shapes this server actually receives:
    /// the standard <c>{results, next}</c> envelope, the surveys' <c>{data}</c> envelope, and the raw
    /// pass-throughs that return a bare array. Covering only the first would leave the raw
    /// pass-through tools recording nothing at all.
    /// </summary>
    private static bool TryFindRecordArray(JsonElement root, out JsonElement records)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            records = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var envelope in (ReadOnlySpan<string>)["results", "data"])
            {
                if (root.TryGetProperty(envelope, out var candidate)
                    && candidate.ValueKind == JsonValueKind.Array)
                {
                    records = candidate;
                    return true;
                }
            }
        }

        records = default;
        return false;
    }

    /// <summary>
    /// A get-by-id returns one bare object. The upstream path names that customer too, so this is
    /// corroboration rather than the only evidence — but reporting "ids unavailable" here would be a
    /// false gap, which is worse than a real one: it sends whoever reads the trail looking for a
    /// limitation that does not exist.
    /// </summary>
    private static AuditedRecords TryReadSingleRecord(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("id", out var single)
        && single.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(single.GetString())
            ? new AuditedRecords([single.GetString()!], 1, IdsAvailable: true)
            : Unavailable;
}
