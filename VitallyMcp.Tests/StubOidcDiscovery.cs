using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace VitallyMcp.Tests;

/// <summary>
/// A stand-in for the upstream provider's OIDC discovery document.
/// </summary>
/// <remarks>
/// <para>
/// The <c>issuer</c> matches the <c>OAuth:Authority</c> the tests configure, because the resolver
/// checks the two against each other (OIDC Discovery §4.3). The <em>endpoints</em> are then
/// deliberately unrelated to it — Entra-shaped, on different hosts, with
/// <c>userinfo_endpoint</c> on a third host again. That is the assertion: no amount of
/// concatenating the issuer with a fixed path can produce them, so a test that sees these values has
/// proved they came from the document.
/// </para>
/// </remarks>
public static class StubOidcDiscovery
{
    /// <summary>
    /// Issuer the stub speaks for. Every factory that stubs discovery must configure this same value
    /// as <c>OAuth:Authority</c>, or the resolver refuses the document.
    /// </summary>
    public const string Issuer = "https://example.auth0.com/";

    public const string AuthorizationEndpoint = "https://login.example-idp.com/tenant-id/oauth2/v2.0/authorize";
    public const string TokenEndpoint = "https://login.example-idp.com/tenant-id/oauth2/v2.0/token";
    public const string JwksUri = "https://login.example-idp.com/tenant-id/discovery/v2.0/keys";
    public const string UserInfoEndpoint = "https://graph.example-idp.com/oidc/userinfo";

    /// <summary>A complete, well-formed discovery document carrying the constants above.</summary>
    public static string Document => BuildDocument();

    /// <summary>
    /// Builds a discovery document, optionally with one property overridden or dropped, so a test can
    /// state exactly which part it is varying.
    /// </summary>
    /// <param name="issuer">Issuer to declare; defaults to <see cref="Issuer"/>.</param>
    /// <param name="omit">Name of a top-level property to leave out entirely.</param>
    public static string BuildDocument(string? issuer = null, string? omit = null)
    {
        var properties = new List<(string Name, string Value)>
        {
            ("issuer", issuer ?? Issuer),
            ("authorization_endpoint", AuthorizationEndpoint),
            ("token_endpoint", TokenEndpoint),
            ("jwks_uri", JwksUri),
            ("userinfo_endpoint", UserInfoEndpoint)
        };

        var body = string.Join(",\n  ", properties
            .Where(p => p.Name != omit)
            .Select(p => $"\"{p.Name}\": \"{p.Value}\""));

        return $"{{\n  {body},\n  \"response_types_supported\": [\"code\"]\n}}";
    }

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
    /// <remarks>
    /// CodeQL's <c>cs/local-not-disposed</c> flags the <see cref="HttpResponseMessage"/> below. It is
    /// a false positive for a message handler: the response is handed to <see cref="HttpClient"/>,
    /// which owns and disposes it. <c>SuppressMessage</c> cannot silence a <c>cs/*</c> rule — see the
    /// remarks on <see cref="TestHelpers"/> — so dismiss it on the alert instead.
    /// </remarks>
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
    /// <param name="body">Document to return on the first call.</param>
    /// <param name="failure">
    /// Exception factory for every later call. Defaults to a transport error; pass a
    /// <see cref="TaskCanceledException"/> factory to model an <see cref="HttpClient"/> timeout,
    /// which is a cancellation rather than a transport failure and takes a different path.
    /// </param>
    /// <remarks>See <see cref="StubHandler"/> on the CodeQL disposal false positive.</remarks>
    public sealed class ThenFailingHandler(string body, Func<Exception>? failure = null) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) > 1)
            {
                throw failure?.Invoke() ?? new HttpRequestException("upstream is down");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>
    /// Captures every request it receives and answers with a canned body — for asserting where the
    /// proxy actually forwarded a request to, not merely what the resolver returned.
    /// </summary>
    /// <remarks>See <see cref="StubHandler"/> on the CodeQL disposal false positive.</remarks>
    public sealed class CapturingHandler(string body, string mediaType = "application/json") : HttpMessageHandler
    {
        /// <summary>Absolute URIs the proxy posted/got, in order.</summary>
        public List<string> RequestedUrls { get; } = [];

        /// <summary>Request bodies, in order, so a forwarded form can be inspected.</summary>
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            lock (RequestedUrls)
            {
                RequestedUrls.Add(request.RequestUri!.ToString());
                RequestBodies.Add(content);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
            };
        }
    }
}
