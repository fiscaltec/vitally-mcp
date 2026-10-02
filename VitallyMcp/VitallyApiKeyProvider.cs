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
        SecretClient? secretClient = null,
        VitallyMetrics? metrics = null)
    {
        _options = options.Value;
        _cache = cache;
        _logger = logger;
        _secretClient = secretClient;
        _metrics = metrics;
    }

    private readonly VitallyMetrics? _metrics;

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

        // Counted only on this Key Vault path (#94). The development key above involves no cache, so
        // counting it would inflate the hit rate with lookups that never happened.
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
        {
            _metrics?.CacheLookup("api_key", hit: true);
            return cached;
        }

        _metrics?.CacheLookup("api_key", hit: false);

        LogBestEffort(() => _logger.LogDebug("Fetching Vitally API key from Key Vault (secret: {SecretRef})", secretRef));
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
            LogBestEffort(() => _logger.LogError(ex,
                "Failed to fetch the Vitally API key from Key Vault (secret: {SecretRef})", secretRef));
            throw;
        }

        var value = response.Value.Value;
        if (value is null)
        {
            LogBestEffort(() => _logger.LogError("Key Vault secret {SecretRef} has no value", secretRef));
            throw new InvalidOperationException($"Key Vault secret '{secretRef}' has no value.");
        }

        _cache.Set(cacheKey, value, _options.SecretCacheDuration);
        return value;
    }

    // Every log call here goes through this. The two failure logs sit immediately before a rethrow,
    // where a sink throwing would replace Azure's exception — the diagnosis — with its own; and the
    // Debug line runs on every uncached fetch, where a throwing sink would fail every tool call. So
    // every exception from the write is swallowed — the same rule as ToolCallFailureLog and
    // AuditLogger.Emit (#94).
    private static void LogBestEffort(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Deliberately ignored.
        }
    }
}
