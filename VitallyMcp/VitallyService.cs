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
        ["meetingTranscripts"] = ["id", "meetingId", "createdAt", "updatedAt"]
    };

    private static readonly string[] FallbackDefaultFields = ["id", "createdAt", "updatedAt"];

    public VitallyService(HttpClient httpClient, IOptions<VitallyServerOptions> options, VitallyApiKeyProvider apiKeyProvider)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _apiKeyProvider = apiKeyProvider;
        _baseUrl = _options.BaseUrl;
    }

    private async Task<string> SendAsync(HttpMethod method, string url, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
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

    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string, string>? additionalParams = null, string? traits = null)
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
        return FilterJsonFields(jsonResponse, fields, resourceType, isListResponse: true, traits);
    }

    public async Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null, string? traits = null)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, resourceType, isListResponse: false, traits);
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

    /// <summary>
    /// Filters JSON response to include only requested fields and traits.
    /// If no fields specified, returns resource-specific default field set.
    /// If no traits specified, excludes traits field entirely to reduce response size.
    /// Only includes fields that actually exist on the resource (via TryGetProperty).
    /// </summary>
    private static string FilterJsonFields(string jsonResponse, string? fields, string resourceType, bool isListResponse, string? traits = null)
    {
        var requestedFields = string.IsNullOrWhiteSpace(fields)
            ? (ResourceDefaultFields.TryGetValue(resourceType, out var defaults) ? defaults : FallbackDefaultFields)
            : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var requestedTraits = string.IsNullOrWhiteSpace(traits)
            ? null
            : traits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
