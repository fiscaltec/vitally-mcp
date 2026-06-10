using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        ["conversations"] = ["id", "externalId", "subject", "authorId", "accountId", "organizationId"],
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
        // Percent-encode keys and values (RFC 3986 query-string encoding via
        // Uri.EscapeDataString — spaces become %20, not +) so user-supplied filter values
        // containing spaces, ampersands, equals signs etc. don't corrupt the URL
        // (e.g. customFieldValue="Acme Corp" -> customFieldValue=Acme%20Corp).
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

        var queryString = string.Join("&", queryParams);
        var url = $"{_baseUrl}/resources/{resourceType}?{queryString}";

        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, defaultsKey ?? resourceType, isListResponse: true, traits);
    }

    public async Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null, string? traits = null)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, resourceType, isListResponse: false, traits);
    }

    /// <summary>
    /// Searches custom object instances via Vitally's <c>/instances/search</c> endpoint, which
    /// accepts exactly one criterion (where <c>customFieldId</c>+<c>customFieldValue</c> are a
    /// single paired criterion). Only the criterion is sent — no <c>limit</c>/<c>from</c>/<c>sortBy</c>,
    /// since the search endpoint's pagination support is not documented. The standard
    /// <c>{results, next}</c> field/trait filtering is applied; <c>next</c> is passed through if present.
    /// </summary>
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
            if (document.RootElement.TryGetProperty("results", out var resultsElement))
            {
                writer.WritePropertyName("results");
                writer.WriteStartArray();

                foreach (var item in resultsElement.EnumerateArray())
                {
                    WriteFilteredObject(writer, item, requestedFields, requestedTraits);
                }

                writer.WriteEndArray();
            }

            if (document.RootElement.TryGetProperty("next", out var nextElement))
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
    /// Extracts the first element of a <c>{results: [...]}</c> response and returns it as a single
    /// filtered object. Returns <c>{"message": notFoundMessage}</c> when results are absent or empty.
    /// </summary>
    private static string FilterSingleFromResults(string jsonResponse, string? fields, string defaultsKey, string? traits, string notFoundMessage)
    {
        using var document = JsonDocument.Parse(jsonResponse);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
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
