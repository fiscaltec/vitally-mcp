namespace VitallyMcp;

public class VitallyConfig
{
    public const string ApiKeyEnvironmentVariable = "VITALLY_API_KEY";
    public const string SubdomainEnvironmentVariable = "VITALLY_SUBDOMAIN";

    public string ApiKey { get; set; } = string.Empty;
    public string Subdomain { get; set; } = string.Empty;

    public static VitallyConfig FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        var subdomain = Environment.GetEnvironmentVariable(SubdomainEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Environment variable '{ApiKeyEnvironmentVariable}' is required but not set. " +
                $"Please configure this in your MCP client settings.");
        }

        if (string.IsNullOrWhiteSpace(subdomain))
        {
            throw new InvalidOperationException(
                $"Environment variable '{SubdomainEnvironmentVariable}' is required but not set. " +
                $"Please configure this in your MCP client settings.");
        }

        return new VitallyConfig
        {
            ApiKey = apiKey,
            Subdomain = subdomain
        };
    }
}
