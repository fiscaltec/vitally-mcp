using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

public class VitallyService
{
    private readonly HttpClient _httpClient;
    private readonly VitallyServerOptions _options;
    private readonly VitallyApiKeyProvider _apiKeyProvider;
    private readonly ToolAuthorizer _authorizer;
    private readonly AuditLogger _audit;
    private readonly string _baseUrl;

    // Resource-specific default fields to return when no fields are specified
    // Tailored to each resource type for optimal balance of usefulness vs response size
    // Note: traits are excluded by default - use the traits parameter to include specific traits
    private static readonly Dictionary<string, string[]> ResourceDefaultFields = new()
    {
        ["accounts"] = ["id", "name", "createdAt", "updatedAt", "externalId", "organizationId", "healthScore", "mrr", "accountOwnerId", "lastSeenTimestamp"],
        ["organizations"] = ["id", "name", "createdAt", "updatedAt", "externalId", "healthScore", "mrr", "lastSeenTimestamp"],
        ["users"] = ["id", "name", "createdAt", "updatedAt", "externalId", "email", "accountId", "organizationId", "lastSeenTimestamp"],
        ["conversations"] = ["id", "externalId", "subject", "status", "source", "authorId", "accountId", "organizationId"],
        ["notes"] = ["id", "createdAt", "updatedAt", "externalId", "subject", "noteDate", "authorId", "accountId", "organizationId", "categoryId", "archivedAt"],
        ["tasks"] = ["id", "name", "createdAt", "updatedAt", "externalId", "dueDate", "completedAt", "assignedToId", "accountId", "organizationId", "archivedAt"],
        ["projects"] = ["id", "name", "createdAt", "updatedAt", "accountId", "organizationId", "archivedAt"],
        ["admins"] = ["id", "name", "email"],
        ["admins/search"] = ["id", "name", "email"],
        ["npsResponses"] = ["id", "externalId", "userId", "score", "feedback", "respondedAt"],
        ["projectTemplates"] = ["id", "name", "createdAt", "updatedAt", "projectCategoryId", "description"],
        ["projectCategories"] = ["id", "name", "createdAt", "updatedAt"],
        ["messages"] = ["id", "type", "externalId", "timestamp", "message", "from", "to"],
        ["customObjects"] = ["id", "name", "createdAt", "updatedAt"],
        ["noteCategories"] = ["id", "name", "createdAt", "updatedAt"],
        ["taskCategories"] = ["id", "name", "createdAt", "updatedAt"],
        ["meetings"] = ["id", "title", "externalId", "startDateTime", "endDateTime", "location", "source", "accountIds", "organizationIds", "participants", "createdAt", "updatedAt"],
        ["meetingTranscripts"] = ["id", "meetingId", "createdAt", "updatedAt"],
        ["customObjectInstances"] = ["id", "name", "externalId", "createdAt", "updatedAt", "organizationId", "customerId", "archivedAt"]
    };

    private static readonly string[] FallbackDefaultFields = ["id", "createdAt", "updatedAt"];

    public VitallyService(HttpClient httpClient, IOptions<VitallyServerOptions> options, VitallyApiKeyProvider apiKeyProvider, ToolAuthorizer authorizer, AuditLogger audit)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _apiKeyProvider = apiKeyProvider;
        _authorizer = authorizer;
        _audit = audit;
        _baseUrl = _options.BaseUrl;
    }

    private async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        // Server-side RBAC backstop: every Vitally call passes through here, so checking the
        // caller's permission against the HTTP verb covers all tools in one place. A denied
        // attempt is itself worth recording in the audit trail.
        try
        {
            await _authorizer.EnsureAuthorizedAsync(method, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            _audit.LogDenied(method, url);
            throw;
        }

        using var request = new HttpRequestMessage(method, url);
        if (content is not null)
        {
            request.Content = content;
        }

        var apiKey = await _apiKeyProvider.GetApiKeyAsync(cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{apiKey}:"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        // Dispose the HttpResponseMessage as soon as we've extracted the body — every Vitally
        // call ends up reading the full body as a string anyway, so there's no benefit to
        // leaking the response to callers (and several callers previously failed to dispose).
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        _audit.LogAction(method, url, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            // EnsureSuccessStatusCode discards the response body, but Vitally returns the
            // actual failure reason in the body (e.g. {"message": "externalId is required"}).
            // Surfacing it gives the LLM something concrete to act on instead of "Response
            // status code does not indicate success".
            var bodySnippet = string.IsNullOrWhiteSpace(body) ? "(empty body)" : Truncate(body, 1024);
            throw new HttpRequestException(
                $"Vitally API returned {(int)response.StatusCode} {response.ReasonPhrase} for {method} {url}. Body: {bodySnippet}",
                inner: null,
                statusCode: response.StatusCode);
        }
        return body;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…(truncated)";

    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string, string>? additionalParams = null, string? traits = null, string? defaultsKey = null)
    {
        var url = BuildListUrl(resourceType, limit, from, sortBy, additionalParams);
        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, defaultsKey ?? resourceType, isListResponse: true, traits);
    }

    // Builds a /resources/{type} list URL. RFC 3986 query encoding via Uri.EscapeDataString
    // (spaces -> %20) so filter values with spaces/&/= don't corrupt the URL. Shared by
    // GetResourcesAsync and the auto-pager.
    private string BuildListUrl(string resourceType, int limit, string? from, string? sortBy, Dictionary<string, string>? additionalParams)
    {
        var queryParams = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrEmpty(from))
            queryParams.Add($"from={Uri.EscapeDataString(from)}");
        if (!string.IsNullOrEmpty(sortBy))
            queryParams.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
        if (additionalParams != null)
        {
            foreach (var param in additionalParams)
            {
                queryParams.Add($"{Uri.EscapeDataString(param.Key)}={Uri.EscapeDataString(param.Value)}");
            }
        }
        return $"{_baseUrl}/resources/{resourceType}?{string.Join("&", queryParams)}";
    }

    public async Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null, string? traits = null)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, resourceType, isListResponse: false, traits);
    }

    /// <summary>
    /// Pages a list endpoint (via from/next) applying a client-side <paramref name="predicate"/> to
    /// each raw item, accumulating matches up to <c>Vitally:MaxAutoPageFetches</c> pages (100/page).
    /// Returns a <c>{results, truncated, pagesFetched}</c> envelope; <c>truncated</c> is true when
    /// the page cap was hit before the endpoint was exhausted. <paramref name="stopBefore"/>
    /// (optional) ends paging early once an item is reached that — given the sort order —
    /// guarantees no further matches.
    /// </summary>
    private async Task<string> GetFilteredAsync(string resourceType, Func<JsonElement, bool> predicate, string? fields, string? sortBy, Dictionary<string, string>? additionalParams, string? traits, string defaultsKey, Func<JsonElement, bool>? stopBefore = null)
    {
        const int pageSize = 100;
        var maxPages = _options.MaxAutoPageFetches;
        var matched = new List<JsonElement>();
        string? from = null;
        var pagesFetched = 0;
        var truncated = false;
        var stopped = false;

        while (true)
        {
            var url = BuildListUrl(resourceType, pageSize, from, sortBy, additionalParams);
            var pageJson = await SendAsync(HttpMethod.Get, url);
            pagesFetched++;

            using (var doc = JsonDocument.Parse(pageJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (stopBefore != null && stopBefore(item)) { stopped = true; break; }
                        if (predicate(item)) matched.Add(item.Clone());
                    }
                }

                from = !stopped && root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
            }

            if (stopped || string.IsNullOrEmpty(from)) break;     // early-stop or exhausted
            if (pagesFetched >= maxPages) { truncated = true; break; }   // cap hit
        }

        var requestedFields = ResolveFields(fields, defaultsKey);
        var requestedTraits = ResolveTraits(traits);

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var item in matched)
        {
            WriteFilteredObject(writer, item, requestedFields, requestedTraits);
        }
        writer.WriteEndArray();
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("pagesFetched", pagesFetched);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Lists a resource filtered to items whose <c>name</c> contains <paramref name="nameContains"/>
    /// (case-insensitive), via the bounded auto-pager. For endpoints Vitally can't filter by name.
    /// </summary>
    public Task<string> GetByNameContainsAsync(string resourceType, string nameContains, string? fields = null, string? traits = null, string? defaultsKey = null, Dictionary<string, string>? additionalParams = null)
    {
        return GetFilteredAsync(
            resourceType,
            item => item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && (n.GetString() ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase),
            fields, sortBy: null, additionalParams, traits, defaultsKey ?? resourceType);
    }

    /// <summary>
    /// Lists a resource filtered to items whose <c>createdAt</c> falls within
    /// [<paramref name="createdAfter"/>, <paramref name="createdBefore"/>] (either bound optional),
    /// via the bounded auto-pager. Pages sorted by <c>createdAt</c> (descending) so paging stops
    /// early once items fall below the lower bound. Bounds must be ISO-8601; throws
    /// <see cref="ArgumentException"/> otherwise (before any HTTP call).
    /// </summary>
    public Task<string> GetByCreatedRangeAsync(string resourceType, string? createdAfter, string? createdBefore, string? fields = null, string? traits = null, string? defaultsKey = null, Dictionary<string, string>? additionalParams = null)
    {
        var after = ParseDateBound(createdAfter, nameof(createdAfter));
        var before = ParseDateBound(createdBefore, nameof(createdBefore));

        bool InRange(JsonElement item)
        {
            if (!item.TryGetProperty("createdAt", out var c) || c.ValueKind != JsonValueKind.String
                || !TryParseTimestamp(c.GetString(), out var dt))
            {
                return false;
            }
            if (after.HasValue && dt < after.Value) return false;
            if (before.HasValue && dt > before.Value) return false;
            return true;
        }

        // createdAt descending => once an item is older than the lower bound, all later items are too.
        Func<JsonElement, bool>? stopBefore = after.HasValue
            ? item => item.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String
                      && TryParseTimestamp(c.GetString(), out var dt) && dt < after.Value
            : null;

        return GetFilteredAsync(resourceType, InRange, fields, sortBy: "createdAt", additionalParams, traits, defaultsKey ?? resourceType, stopBefore);
    }

    private static DateTimeOffset? ParseDateBound(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!TryParseTimestamp(value, out var dt))
        {
            throw new ArgumentException($"{paramName} must be an ISO-8601 date/time (got '{value}').", paramName);
        }
        return dt;
    }

    // Culture-invariant timestamp parse shared by the date-bound validation and the per-item
    // createdAt filtering, so item filtering can never become locale-dependent. ISO-8601 values
    // carrying an explicit offset (e.g. "…Z") keep it; offset-less values are treated as UTC.
    private static bool TryParseTimestamp(string? value, out DateTimeOffset result) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out result);

    /// <summary>
    /// Issues a custom object instance search against Vitally's <c>/instances/search</c> endpoint
    /// with the supplied <paramref name="criteria"/>. Only the criteria are sent — no
    /// <c>limit</c>/<c>from</c>/<c>sortBy</c>. Unlike the list endpoints, <c>/search</c> returns a
    /// bare <c>[...]</c> array with no pagination cursor; the results are field/trait-filtered and
    /// wrapped in the standard <c>{results}</c> envelope so the tool output stays uniform.
    /// </summary>
    /// <remarks>
    /// Vitally accepts exactly ONE criterion (with <c>customFieldId</c>+<c>customFieldValue</c> as a
    /// single paired criterion). Enforcing that rule is the caller's responsibility:
    /// <c>CustomObjectsTools.BuildInstanceSearchCriteria</c> validates it from the typed tool
    /// parameters, where the pairing is unambiguous — a raw key/value dictionary cannot tell a valid
    /// <c>customFieldId</c>+<c>customFieldValue</c> pair apart from two separate criteria, so this
    /// transport-level method does not re-attempt that check. It only rejects an empty dictionary
    /// (which cannot form a valid query) and forwards any other shape as-is, letting Vitally return
    /// its own validation error.
    /// </remarks>
    public async Task<string> SearchCustomObjectInstancesAsync(string customObjectId, IReadOnlyDictionary<string, string> criteria, string? fields = null, string? traits = null)
    {
        if (criteria.Count == 0)
        {
            throw new ArgumentException("At least one search criterion is required.", nameof(criteria));
        }

        var query = string.Join("&", criteria.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_baseUrl}/resources/customObjects/{customObjectId}/instances/search?{query}";

        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, "customObjectInstances", isListResponse: true, traits);
    }

    /// <summary>
    /// Reads a single custom object instance by id. Vitally has no direct single-instance GET, so
    /// this uses <c>/instances/search?id=…</c> and unwraps the single result. Returns a clear
    /// not-found message (not an exception) when the search yields no match (HTTP 200, empty results).
    /// </summary>
    public async Task<string> GetCustomObjectInstanceByIdAsync(string customObjectId, string instanceId, string? fields = null, string? traits = null)
    {
        var url = $"{_baseUrl}/resources/customObjects/{customObjectId}/instances/search?id={Uri.EscapeDataString(instanceId)}";
        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterSingleFromResults(jsonResponse, fields, "customObjectInstances", traits,
            notFoundMessage: $"No custom object instance found with id {instanceId}");
    }

    /// <summary>
    /// Composite read: returns one organisation with its curated rollup traits, plus its open goals and
    /// product-feedback custom-object instances, in a single response. Generic and tenant-agnostic —
    /// the curated trait set and the two custom-object names are supplied by the caller (the tool layer
    /// owns those tenant-specific defaults). Makes 4 upstream calls: org get-by-id (with traits), one
    /// customObjects list to resolve both object names to ids, and two organisation-scoped instance
    /// searches. Goals/product-feedback are each captured independently: a failure of one (object name
    /// not found, upstream error, malformed body) yields a { "error": ... } for that section only, so a
    /// single sub-failure never sinks the whole summary. A failed org get-by-id propagates (no summary
    /// without it).
    /// </summary>
    public async Task<string> GetOrganizationSummaryAsync(
        string organizationId, string? traitsCsv, string goalsObjectName, string productFeedbackObjectName)
    {
        // Organisation: default org fields PLUS traits, filtered to the curated/overridden set.
        var orgFields = string.Join(",", ResourceDefaultFields["organizations"]) + ",traits";
        var orgJson = await GetResourceByIdAsync("organizations", organizationId, orgFields, traitsCsv);

        // Resolve both object names to ids in one list call. A failure here is recorded and surfaces as a
        // per-section error below (rather than throwing and losing the organisation we already fetched).
        Dictionary<string, string> nameToId;
        string? resolveError = null;
        try
        {
            nameToId = await ResolveCustomObjectIdsAsync();
        }
        catch (Exception ex)
        {
            nameToId = new Dictionary<string, string>(StringComparer.Ordinal);
            resolveError = ex.Message;
        }

        var goals = await BuildInstanceSectionAsync(nameToId, goalsObjectName, organizationId, resolveError);
        var productFeedback = await BuildInstanceSectionAsync(nameToId, productFeedbackObjectName, organizationId, resolveError);

        var root = new JsonObject
        {
            ["organization"] = JsonNode.Parse(orgJson),
            ["goals"] = goals,
            ["productFeedback"] = productFeedback,
        };
        return root.ToJsonString();
    }

    // Fetches the customObjects catalogue (single list call) and maps name -> id.
    private async Task<Dictionary<string, string>> ResolveCustomObjectIdsAsync()
    {
        var json = await GetResourcesAsync("customObjects", limit: 100);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in results.EnumerateArray())
            {
                if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                    element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    map[name.GetString()!] = id.GetString()!;
                }
            }
        }
        return map;
    }

    // Produces one summary section: the organisation-scoped instances of the named custom object, or a
    // { "error": ... } node if the object name can't be resolved or the search fails.
    private async Task<JsonNode> BuildInstanceSectionAsync(
        IReadOnlyDictionary<string, string> nameToId, string objectName, string organizationId, string? resolveError)
    {
        try
        {
            if (resolveError is not null)
            {
                throw new InvalidOperationException($"could not resolve custom objects: {resolveError}");
            }
            if (!nameToId.TryGetValue(objectName, out var objectId))
            {
                throw new InvalidOperationException($"custom object '{objectName}' not found");
            }
            var json = await SearchCustomObjectInstancesAsync(
                objectId, new Dictionary<string, string> { ["organizationId"] = organizationId });
            return JsonNode.Parse(json)!;
        }
        catch (Exception ex)
        {
            return new JsonObject { ["error"] = ex.Message };
        }
    }

    public async Task<string> CreateResourceAsync(string resourceType, string jsonBody)
    {
        var url = $"{_baseUrl}/resources/{resourceType}";
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return await SendAsync(HttpMethod.Post, url, content);
    }

    public async Task<string> UpdateResourceAsync(string resourceType, string id, string jsonBody)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return await SendAsync(HttpMethod.Put, url, content);
    }

    public async Task<string> DeleteResourceAsync(string resourceType, string id)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        return await SendAsync(HttpMethod.Delete, url);
    }

    /// <summary>
    /// GET an arbitrary path under /resources with no field filtering.
    /// Use for endpoints whose response shape differs from the standard {results, next}
    /// envelope (e.g. surveys use {data, next}, customFields returns a bare array).
    /// </summary>
    public async Task<string> GetRawAsync(string path, Dictionary<string, string>? queryParams = null)
    {
        var url = $"{_baseUrl}/resources/{path}";
        if (queryParams != null && queryParams.Count > 0)
        {
            var query = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
            url = $"{url}?{query}";
        }

        return await SendAsync(HttpMethod.Get, url);
    }

    /// <summary>
    /// GETs a bare-array endpoint (e.g. customFields) and returns only the elements whose
    /// <c>label</c> or <c>path</c> contains <paramref name="nameContains"/> (case-insensitive).
    /// No paging — the whole array is one response. Returns the filtered array unchanged in shape.
    /// </summary>
    public async Task<string> GetRawArrayFilteredAsync(string path, Dictionary<string, string> queryParams, string nameContains)
    {
        var json = await GetRawAsync(path, queryParams);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return json; // unexpected shape — pass through unchanged
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var p = item.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
            if ((label ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase)
                || (p ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                item.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// POST raw JSON to an arbitrary path under /resources (for sub-resources that don't
    /// fit the {resourceType, id} pattern, e.g. meeting participants).
    /// </summary>
    public async Task<string> PostRawAsync(string path, string jsonBody)
    {
        var url = $"{_baseUrl}/resources/{path}";
        var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return await SendAsync(HttpMethod.Post, url, content);
    }

    /// <summary>
    /// DELETE an arbitrary path under /resources (for sub-resources, e.g. meeting participants).
    /// </summary>
    public async Task<string> DeleteRawAsync(string path)
    {
        var url = $"{_baseUrl}/resources/{path}";

        return await SendAsync(HttpMethod.Delete, url);
    }

    private static string[] ResolveFields(string? fields, string defaultsKey) =>
        string.IsNullOrWhiteSpace(fields)
            ? (ResourceDefaultFields.TryGetValue(defaultsKey, out var defaults) ? defaults : FallbackDefaultFields)
            : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[]? ResolveTraits(string? traits) =>
        string.IsNullOrWhiteSpace(traits)
            ? null
            : traits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Filters JSON response to include only requested fields and traits.
    /// If no fields specified, returns resource-specific default field set.
    /// If no traits specified, excludes traits field entirely to reduce response size.
    /// Only includes fields that actually exist on the resource (via TryGetProperty).
    /// </summary>
    private static string FilterJsonFields(string jsonResponse, string? fields, string resourceType, bool isListResponse, string? traits = null)
    {
        var requestedFields = ResolveFields(fields, resourceType);
        var requestedTraits = ResolveTraits(traits);

        using var document = JsonDocument.Parse(jsonResponse);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        if (isListResponse)
        {
            // List endpoints return a {results, next} envelope, but the customObjects
            // /instances/search endpoint returns a bare [...] array. Accept both, and always emit
            // a {results, ...} envelope so callers get a uniform shape regardless of the source.
            var root = document.RootElement;
            JsonElement resultsElement;
            bool hasResults;
            if (root.ValueKind == JsonValueKind.Array)
            {
                resultsElement = root;
                hasResults = true;
            }
            else
            {
                hasResults = root.TryGetProperty("results", out resultsElement);
            }

            if (hasResults && resultsElement.ValueKind == JsonValueKind.Array)
            {
                writer.WritePropertyName("results");
                writer.WriteStartArray();

                foreach (var item in resultsElement.EnumerateArray())
                {
                    WriteFilteredObject(writer, item, requestedFields, requestedTraits);
                }

                writer.WriteEndArray();
            }

            // A bare-array (search) response carries no pagination cursor; only the
            // {results, next} envelope does.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("next", out var nextElement))
            {
                writer.WritePropertyName("next");
                nextElement.WriteTo(writer);
            }
        }
        else
        {
            WriteFilteredFields(writer, document.RootElement, requestedFields, requestedTraits);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Extracts the first instance from a search response and returns it as a single filtered
    /// object. Vitally's <c>/instances/search</c> returns a bare <c>[...]</c> array; the list
    /// endpoints return <c>{results: [...]}</c>. Both are accepted. Returns
    /// <c>{"message": notFoundMessage}</c> when the response holds no instances.
    /// </summary>
    private static string FilterSingleFromResults(string jsonResponse, string? fields, string defaultsKey, string? traits, string notFoundMessage)
    {
        using var document = JsonDocument.Parse(jsonResponse);
        var root = document.RootElement;
        JsonElement results;
        if (root.ValueKind == JsonValueKind.Array)
        {
            results = root;
        }
        else
        {
            root.TryGetProperty("results", out results);
        }

        if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            return JsonSerializer.Serialize(new { message = notFoundMessage });
        }

        var requestedFields = ResolveFields(fields, defaultsKey);
        var requestedTraits = ResolveTraits(traits);

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteFilteredObject(writer, results[0], requestedFields, requestedTraits);
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFilteredObject(Utf8JsonWriter writer, JsonElement element, string[] fields, string[]? requestedTraits)
    {
        writer.WriteStartObject();
        WriteFilteredFields(writer, element, fields, requestedTraits);
        writer.WriteEndObject();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1860",
        Justification = "JsonElement.TryGetProperty uses an out parameter that LINQ Where cannot expose without a redundant second call. The explicit foreach is clearer and more efficient than the LINQ rewrite.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "cs/linq/missed-where",
        Justification = "Same as above — TryGetProperty out parameter pattern.")]
    private static void WriteFilteredFields(Utf8JsonWriter writer, JsonElement element, string[] fields, string[]? requestedTraits)
    {
        foreach (var field in fields)
        {
            if (element.TryGetProperty(field, out var value))
            {
                if (field.Equals("traits", StringComparison.OrdinalIgnoreCase) && requestedTraits != null && value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName(field);
                    WriteFilteredTraits(writer, value, requestedTraits);
                }
                else
                {
                    writer.WritePropertyName(field);
                    value.WriteTo(writer);
                }
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "cs/linq/missed-where",
        Justification = "JsonElement.TryGetProperty out parameter doesn't translate to LINQ Where without a redundant second call.")]
    private static void WriteFilteredTraits(Utf8JsonWriter writer, JsonElement traitsElement, string[] requestedTraits)
    {
        writer.WriteStartObject();

        foreach (var traitName in requestedTraits)
        {
            if (traitsElement.TryGetProperty(traitName, out var traitValue))
            {
                writer.WritePropertyName(traitName);
                traitValue.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
