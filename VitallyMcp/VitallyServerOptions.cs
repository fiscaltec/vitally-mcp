namespace VitallyMcp;

public class VitallyServerOptions
{
    public const string SectionName = "Vitally";

    public const string RegionUs = "US";
    public const string RegionEu = "EU";

    public string Region { get; set; } = RegionEu;

    public string? Subdomain { get; set; }

    public string? KeyVaultUri { get; set; }

    public string DefaultSecretRef { get; set; } = "vitally-shared";

    public TimeSpan SecretCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    public string? DevelopmentApiKey { get; set; }

    public string BaseUrl => Region.Equals(RegionUs, StringComparison.OrdinalIgnoreCase)
        ? $"https://{Subdomain}.rest.vitally.io"
        : "https://rest.vitally-eu.io";

    public void Validate()
    {
        var region = (Region ?? string.Empty).Trim().ToUpperInvariant();
        if (region != RegionUs && region != RegionEu)
        {
            throw new InvalidOperationException(
                $"Vitally:Region must be 'US' or 'EU' (got '{Region}').");
        }
        Region = region;

        if (region == RegionUs && string.IsNullOrWhiteSpace(Subdomain))
        {
            throw new InvalidOperationException(
                "Vitally:Subdomain is required when Vitally:Region is 'US'.");
        }

        KeyVaultUri = KeyVaultUri?.Trim();
        DevelopmentApiKey = DevelopmentApiKey?.Trim();

        if (!string.IsNullOrWhiteSpace(KeyVaultUri)
            && (!Uri.TryCreate(KeyVaultUri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Vitally:KeyVaultUri must be an absolute https URI (got '{KeyVaultUri}').");
        }

        if (string.IsNullOrWhiteSpace(KeyVaultUri) && string.IsNullOrWhiteSpace(DevelopmentApiKey))
        {
            throw new InvalidOperationException(
                "Either Vitally:KeyVaultUri must be set (production) or Vitally:DevelopmentApiKey must be set (local dev).");
        }
    }
}
