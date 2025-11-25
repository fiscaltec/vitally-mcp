using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VitallyMcp;

public class VitallyService
{
    private readonly HttpClient _httpClient;
    private readonly VitallyConfig _config;
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
        ["admins"] = ["id", "name", "email"]
    };


    // Fallback default fields for unknown resource types
    private static readonly string[] FallbackDefaultFields = ["id", "createdAt", "updatedAt"];

    public VitallyService(HttpClient httpClient, VitallyConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        _baseUrl = $"https://{_config.Subdomain}.rest.vitally.io";

        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.ApiKey}:"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
    }

    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string, string>? additionalParams = null, string? traits = null)
    {
        var queryParams = new List<string> { $"limit={limit}" };

        if (!string.IsNullOrEmpty(from))
            queryParams.Add($"from={from}");

        if (!string.IsNullOrEmpty(sortBy))
            queryParams.Add($"sortBy={sortBy}");

        // Add any resource-specific parameters (e.g., status for accounts)
        if (additionalParams != null)
        {
            foreach (var param in additionalParams)
            {
                queryParams.Add($"{param.Key}={param.Value}");
            }
        }

        var queryString = string.Join("&", queryParams);
        var url = $"{_baseUrl}/resources/{resourceType}?{queryString}";

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();

        // Apply client-side field and trait filtering with resource-specific defaults
        return FilterJsonFields(jsonResponse, fields, resourceType, isListResponse: true, traits);
    }

    public async Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null, string? traits = null)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();

        // Apply client-side field and trait filtering with resource-specific defaults
        return FilterJsonFields(jsonResponse, fields, resourceType, isListResponse: false, traits);
    }

    /// <summary>
    /// Filters JSON response to include only requested fields and traits.
    /// If no fields specified, returns resource-specific default field set.
    /// If no traits specified, excludes traits field entirely to reduce response size.
    /// Only includes fields that actually exist on the resource (via TryGetProperty).
    /// </summary>
    private static string FilterJsonFields(string jsonResponse, string? fields, string resourceType, bool isListResponse, string? traits = null)
    {
        // Parse the fields parameter or use resource-specific defaults
        var requestedFields = string.IsNullOrWhiteSpace(fields)
            ? (ResourceDefaultFields.TryGetValue(resourceType, out var defaults) ? defaults : FallbackDefaultFields)
            : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Parse the traits parameter if specified
        var requestedTraits = string.IsNullOrWhiteSpace(traits)
            ? null
            : traits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        using var document = JsonDocument.Parse(jsonResponse);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();

        if (isListResponse)
        {
            // Handle list response with results array
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

            // Preserve pagination cursor
            if (document.RootElement.TryGetProperty("next", out var nextElement))
            {
                writer.WritePropertyName("next");
                nextElement.WriteTo(writer);
            }
        }
        else
        {
            // Handle single object response - write filtered fields directly
            WriteFilteredFields(writer, document.RootElement, requestedFields, requestedTraits);
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a filtered JSON object containing only the requested fields
    /// </summary>
    private static void WriteFilteredObject(Utf8JsonWriter writer, JsonElement element, string[] fields, string[]? requestedTraits)
    {
        writer.WriteStartObject();
        WriteFilteredFields(writer, element, fields, requestedTraits);
        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes filtered fields to JSON writer, handling special trait filtering logic
    /// </summary>
    private static void WriteFilteredFields(Utf8JsonWriter writer, JsonElement element, string[] fields, string[]? requestedTraits)
    {
        foreach (var field in fields)
        {
            if (element.TryGetProperty(field, out var value))
            {
                // Special handling for traits field
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

    /// <summary>
    /// Writes a filtered traits object containing only the requested trait keys
    /// </summary>
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
