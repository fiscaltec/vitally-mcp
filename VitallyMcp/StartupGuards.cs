namespace VitallyMcp;

/// <summary>
/// Fail-fast configuration guards checked at startup, before the app serves traffic.
/// </summary>
public static class StartupGuards
{
    /// <summary>
    /// Refuses a configuration that disables authentication while a Key Vault is configured — that
    /// combination looks like a production deployment accidentally running unauthenticated. NoAuth is
    /// for local development only (which has no Key Vault and uses a development API key instead).
    /// </summary>
    public static void EnsureSafeAuthConfig(bool noAuth, string? keyVaultUri)
    {
        if (noAuth && !string.IsNullOrWhiteSpace(keyVaultUri))
        {
            throw new InvalidOperationException(
                "OAuth:NoAuth=true together with a Vitally:KeyVaultUri is refused — this looks like a " +
                "production deployment running unauthenticated. NoAuth is for local development only " +
                "(no Key Vault). Remove NoAuth in any environment that uses Key Vault.");
        }
    }
}
