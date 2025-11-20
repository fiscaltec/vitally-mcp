using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VitallyMcp;

public class VitallyService
{
    private readonly HttpClient _httpClient;
    private readonly VitallyConfig _config;
    private readonly string _baseUrl;

    public VitallyService(HttpClient httpClient, VitallyConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        _baseUrl = $"https://{_config.Subdomain}.rest.vitally.io";

        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.ApiKey}:"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
    }

    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? cursor = null, string? fields = null)
    {
        var queryParams = new List<string> { $"limit={limit}" };

        if (!string.IsNullOrEmpty(cursor))
            queryParams.Add($"cursor={cursor}");

        if (!string.IsNullOrEmpty(fields))
            queryParams.Add($"fields={fields}");

        var queryString = string.Join("&", queryParams);
        var url = $"{_baseUrl}/resources/{resourceType}?{queryString}";

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    public async Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null)
    {
        var url = $"{_baseUrl}/resources/{resourceType}/{id}";

        if (!string.IsNullOrEmpty(fields))
            url += $"?fields={fields}";

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }
}
