using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VitallyMcp;

public class VitallyService
{
    private readonly HttpClient _httpClient;
    private readonly VitallyConfig _config;
    private readonly string _baseUrl;

    // Default fields to return when no fields are specified
    private static readonly string[] DefaultFields = { "id", "name", "createdAt", "updatedAt" };

    public VitallyService(HttpClient httpClient, VitallyConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        _baseUrl = $"https://{_config.Subdomain}.rest.vitally.io";

        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.ApiKey}:"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
    }

    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string, string>? additionalParams = null)
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

        // Apply client-side field filtering
        return FilterJsonFields(jsonResponse, fields, isListResponse: true);
    }

    public async Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var jsonResponse = await response.Content.ReadAsStringAsync();

        // Apply client-side field filtering
        return FilterJsonFields(jsonResponse, fields, isListResponse: false);
    }

    /// <summary>
    /// Filters JSON response to include only requested fields.
    /// If no fields specified, returns default minimal set: id, name, createdAt, updatedAt
    /// </summary>
    private static string FilterJsonFields(string jsonResponse, string? fields, bool isListResponse)
    {
        // Parse the fields parameter or use defaults
        var requestedFields = string.IsNullOrWhiteSpace(fields)
            ? DefaultFields
            : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
                    WriteFilteredObject(writer, item, requestedFields);
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
            foreach (var field in requestedFields)
            {
                if (document.RootElement.TryGetProperty(field, out var value))
                {
                    writer.WritePropertyName(field);
                    value.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a filtered JSON object containing only the requested fields
    /// </summary>
    private static void WriteFilteredObject(Utf8JsonWriter writer, JsonElement element, string[] fields)
    {
        writer.WriteStartObject();

        foreach (var field in fields)
        {
            if (element.TryGetProperty(field, out var value))
            {
                writer.WritePropertyName(field);
                value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }
}
