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

    /// <summary>
    /// Resolves the upstream OIDC discovery document once at startup so a provider that is
    /// unreachable — or publishing a document missing an endpoint the proxy needs — refuses to
    /// start. Two of those endpoints are republished to MCP clients in our RFC 8414 document, so
    /// booting without having verified them means advertising unverified endpoints as fact; a
    /// refusal to start is the lesser failure, and a loud one. No-op when the OAuth proxy is off,
    /// because nothing then reads the document.
    /// </summary>
    /// <param name="metadata">Resolver whose cache this warms.</param>
    /// <param name="proxyEnabled">Whether <c>OAuth:SharedClientId</c> is set.</param>
    /// <param name="timeout">Bound on the fetch, so an unresponsive provider fails rather than hangs.</param>
    public static async Task EnsureUpstreamOidcEndpointsAsync(
        UpstreamOidcMetadata metadata,
        bool proxyEnabled,
        TimeSpan timeout)
    {
        if (!proxyEnabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            _ = await metadata.GetAsync(cts.Token);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "The upstream OIDC discovery document could not be resolved, so the OAuth proxy cannot " +
                "determine the provider's authorize, token, jwks and userinfo endpoints. Refusing to start " +
                "rather than advertise endpoints that were never verified. Check that OAuth:Authority is the " +
                "provider's issuer URL and that its /.well-known/openid-configuration is reachable from " +
                $"this host. Underlying failure: {ex.Message}",
                ex);
        }
    }
}
