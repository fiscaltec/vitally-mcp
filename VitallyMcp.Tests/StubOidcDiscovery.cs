using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace VitallyMcp.Tests;

/// <summary>
/// A stand-in for the upstream provider's OIDC discovery document.
/// </summary>
/// <remarks>
/// The endpoint shapes here are deliberately <em>Entra</em>-shaped rather than Auth0-shaped, even
/// though the tests configure an Auth0-looking <c>OAuth:Authority</c>. That mismatch is the
/// assertion: no amount of concatenating <c>Authority</c> with a fixed path can produce these, so a
/// test that sees them has proved the value came from the document. <c>userinfo_endpoint</c> is on
/// a different host again, which is the case no <c>Authority</c> value can ever cover.
/// </remarks>
public static class StubOidcDiscovery
{
    public const string AuthorizationEndpoint = "https://login.example-idp.com/tenant-id/oauth2/v2.0/authorize";
    public const string TokenEndpoint = "https://login.example-idp.com/tenant-id/oauth2/v2.0/token";
    public const string JwksUri = "https://login.example-idp.com/tenant-id/discovery/v2.0/keys";
    public const string UserInfoEndpoint = "https://graph.example-idp.com/oidc/userinfo";

    /// <summary>A complete, well-formed discovery document carrying the four constants above.</summary>
    public static string Document => $$"""
    {
      "issuer": "https://login.example-idp.com/tenant-id/v2.0",
      "authorization_endpoint": "{{AuthorizationEndpoint}}",
      "token_endpoint": "{{TokenEndpoint}}",
      "jwks_uri": "{{JwksUri}}",
      "userinfo_endpoint": "{{UserInfoEndpoint}}",
      "response_types_supported": ["code"],
      "grant_types_supported": ["authorization_code", "refresh_token"]
    }
    """;

    /// <summary>
    /// Points the resolver's named <see cref="HttpClient"/> at a canned response so an integration
    /// test never reaches a real identity provider. Call from a
    /// <c>WebApplicationFactory</c> host builder; the primary handler registered last wins, and
    /// Program.cs registers none, so this replaces the socket handler outright.
    /// </summary>
    public static void UseStubDiscovery(this IServiceCollection services, string? document = null)
    {
        var body = document ?? Document;
        services.AddHttpClient(UpstreamOidcMetadata.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(body, HttpStatusCode.OK));
    }

    /// <summary>
    /// Returns the supplied body for every request and counts the calls, so a test can assert that
    /// a second resolve was served from cache rather than from the wire.
    /// </summary>
    public sealed class StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>Requested URLs, in order, for asserting the well-known path we construct.</summary>
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            lock (RequestedUrls)
            {
                RequestedUrls.Add(request.RequestUri!.ToString());
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>Throws on every request — the unreachable-provider case.</summary>
    public sealed class FailingHandler(string message = "connection refused") : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            throw new HttpRequestException(message);
        }
    }

    /// <summary>
    /// Succeeds once, then fails — for the refresh-after-a-good-start path, where the resolver must
    /// keep serving what it already verified instead of taking the proxy down.
    /// </summary>
    public sealed class ThenFailingHandler(string body) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) > 1)
            {
                throw new HttpRequestException("upstream is down");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
