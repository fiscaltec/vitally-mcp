using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Resolves the Vitally API key for the current request.
/// Order of resolution:
///   1. If running without Key Vault configured, return the configured DevelopmentApiKey (local dev only).
///   2. Read the secret reference from the authenticated user's <see cref="VitallyServerOptions.SecretRefClaim"/> claim.
///   3. If the claim is missing, fall back to <see cref="VitallyServerOptions.DefaultSecretRef"/>.
///   4. Fetch the secret from Key Vault, caching the value for <see cref="VitallyServerOptions.SecretCacheDuration"/>.
/// </summary>
public class VitallyApiKeyProvider
{
    private readonly VitallyServerOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMemoryCache _cache;
    private readonly SecretClient? _secretClient;
    private readonly ILogger<VitallyApiKeyProvider> _logger;

    public VitallyApiKeyProvider(
        IOptions<VitallyServerOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IMemoryCache cache,
        ILogger<VitallyApiKeyProvider> logger,
        SecretClient? secretClient = null)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _cache = cache;
        _logger = logger;
        _secretClient = secretClient;
    }

    public async Task<string> GetApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_secretClient is null)
        {
            if (string.IsNullOrWhiteSpace(_options.DevelopmentApiKey))
            {
                throw new InvalidOperationException(
                    "Vitally API key cannot be resolved: no Key Vault client registered and no DevelopmentApiKey configured.");
            }
            return _options.DevelopmentApiKey;
        }

        var secretRef = ResolveSecretRef();
        var cacheKey = $"vitally-api-key::{secretRef}";

        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        _logger.LogDebug("Fetching Vitally API key from Key Vault (secret: {SecretRef})", secretRef);
        var response = await _secretClient.GetSecretAsync(secretRef, cancellationToken: cancellationToken);
        var value = response.Value.Value
            ?? throw new InvalidOperationException($"Key Vault secret '{secretRef}' has no value.");

        _cache.Set(cacheKey, value, _options.SecretCacheDuration);
        return value;
    }

    private string ResolveSecretRef()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        var claim = user?.FindFirst(_options.SecretRefClaim)?.Value;
        return string.IsNullOrWhiteSpace(claim) ? _options.DefaultSecretRef : claim;
    }
}
