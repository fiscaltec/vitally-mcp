using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp;

/// <summary>
/// Resolves the Vitally API key for the current request.
/// Order of resolution:
///   1. If running without Key Vault configured, return the configured DevelopmentApiKey (local dev only).
///   2. Fetch <see cref="VitallyServerOptions.DefaultSecretRef"/> from Key Vault, caching for
///      <see cref="VitallyServerOptions.SecretCacheDuration"/>.
/// </summary>
public class VitallyApiKeyProvider
{
    private readonly VitallyServerOptions _options;
    private readonly IMemoryCache _cache;
    private readonly SecretClient? _secretClient;
    private readonly ILogger<VitallyApiKeyProvider> _logger;

    public VitallyApiKeyProvider(
        IOptions<VitallyServerOptions> options,
        IMemoryCache cache,
        ILogger<VitallyApiKeyProvider> logger,
        SecretClient? secretClient = null)
    {
        _options = options.Value;
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

        var secretRef = _options.DefaultSecretRef;
        var cacheKey = $"vitally-api-key::{secretRef}";

        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        _logger.LogDebug("Fetching Vitally API key from Key Vault (secret: {SecretRef})", secretRef);
        // Logged here because nothing else can say why (#94). Every tool call depends on this key, so
        // a failure fails every call — and the tool-call failure log sees only an exception type,
        // which does not name the secret or distinguish a missing role from an unreachable vault.
        //
        // The exception IS attached, unlike the tool-call log: a Key Vault error carries Azure's own
        // status and error code, never the secret value and never customer data, and that detail is
        // the diagnosis. A cancelled caller is not a vault fault and is left unlogged.
        Azure.Response<KeyVaultSecret> response;
        try
        {
            response = await _secretClient.GetSecretAsync(secretRef, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to fetch the Vitally API key from Key Vault (secret: {SecretRef})", secretRef);
            throw;
        }

        var value = response.Value.Value;
        if (value is null)
        {
            _logger.LogError("Key Vault secret {SecretRef} has no value", secretRef);
            throw new InvalidOperationException($"Key Vault secret '{secretRef}' has no value.");
        }

        _cache.Set(cacheKey, value, _options.SecretCacheDuration);
        return value;
    }
}
