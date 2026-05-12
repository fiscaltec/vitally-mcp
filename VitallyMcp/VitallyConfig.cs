namespace VitallyMcp;

public class VitallyConfig
{
    public const string ApiKeyEnvironmentVariable = "VITALLY_API_KEY";
    public const string SubdomainEnvironmentVariable = "VITALLY_SUBDOMAIN";
    public const string RegionEnvironmentVariable = "VITALLY_REGION";

    public const string RegionUs = "US";
    public const string RegionEu = "EU";

    public string ApiKey { get; set; } = string.Empty;
    public string Subdomain { get; set; } = string.Empty;
    public string Region { get; set; } = RegionEu;

    public static VitallyConfig FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        var subdomain = Environment.GetEnvironmentVariable(SubdomainEnvironmentVariable);
        var rawRegion = Environment.GetEnvironmentVariable(RegionEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"Environment variable '{ApiKeyEnvironmentVariable}' is required but not set. " +
                $"Please configure this in your MCP client settings.");
        }

        var region = string.IsNullOrWhiteSpace(rawRegion)
            ? RegionEu
            : rawRegion.Trim().ToUpperInvariant();

        if (region != RegionUs && region != RegionEu)
        {
            throw new InvalidOperationException(
                $"Environment variable '{RegionEnvironmentVariable}' must be 'US' or 'EU' (got '{rawRegion}').");
        }

        // The US instance is per-tenant ({subdomain}.rest.vitally.io), so the subdomain is required.
        // The EU instance is a single host (rest.vitally-eu.io) and has no subdomain.
        if (region == RegionUs && string.IsNullOrWhiteSpace(subdomain))
        {
            throw new InvalidOperationException(
                $"Environment variable '{SubdomainEnvironmentVariable}' is required for the US region but not set. " +
                $"Please configure this in your MCP client settings.");
        }

        return new VitallyConfig
        {
            ApiKey = apiKey,
            Subdomain = subdomain ?? string.Empty,
            Region = region
        };
    }
}
