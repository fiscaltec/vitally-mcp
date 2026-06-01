using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VitallyMcp;

var builder = WebApplication.CreateBuilder(args);

// PostConfigure + a forced IOptions resolution after WebApplicationBuilder.Build() gives
// us fail-fast startup validation without the boilerplate of a separate IValidateOptions
// implementation. If Validate() throws, the app crashes immediately after Build() rather
// than serving requests with bad config.
builder.Services.AddOptions<VitallyServerOptions>()
    .Bind(builder.Configuration.GetSection(VitallyServerOptions.SectionName))
    .PostConfigure(o => o.Validate());

builder.Services.AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection(OAuthOptions.SectionName))
    .PostConfigure(o => o.Validate());

builder.Services.AddOptions<ToolAuthorizationOptions>()
    .Bind(builder.Configuration.GetSection(ToolAuthorizationOptions.SectionName))
    .PostConfigure(o => o.Validate());

builder.Services.AddOptions<AuditOptions>()
    .Bind(builder.Configuration.GetSection(AuditOptions.SectionName));

builder.Services.AddMemoryCache();

// Needed so ToolAuthorizer can read the authenticated ClaimsPrincipal inside tool invocations.
builder.Services.AddHttpContextAccessor();

// SecretClient registration uses the *validated* options (via IOptions) rather than raw
// config, so trimmed/URI-checked KeyVaultUri is what's constructed. Conditional on the
// raw config being present so the registration only fires when KV is actually configured.
var vitallySection = builder.Configuration.GetSection(VitallyServerOptions.SectionName);
if (!string.IsNullOrWhiteSpace(vitallySection["KeyVaultUri"]))
{
    builder.Services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<VitallyServerOptions>>().Value;
        return new SecretClient(new Uri(opts.KeyVaultUri!), new DefaultAzureCredential());
    });
}

builder.Services.AddScoped<VitallyApiKeyProvider>();
builder.Services.AddScoped<ToolAuthorizer>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddTransient<VitallyRateLimitHandler>();

builder.Services.AddHttpClient<VitallyService>()
    .AddHttpMessageHandler<VitallyRateLimitHandler>();

var oauthSection = builder.Configuration.GetSection(OAuthOptions.SectionName);
var noAuth = oauthSection.GetValue<bool>("NoAuth");

if (!noAuth)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer();

    // Configure JwtBearer from the *validated* OAuthOptions (trimmed/URI-checked by
    // OAuthOptions.Validate) rather than the raw IConfiguration values. This guarantees
    // that what JwtBearer sees matches what passed validation.
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<OAuthOptions>>((jwt, oauth) =>
        {
            jwt.Authority = oauth.Value.Authority;
            jwt.Audience = oauth.Value.Audience;
        });

    builder.Services.AddAuthorization();
}

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

// Honour X-Forwarded-Proto / X-Forwarded-Host from Container Apps ingress so absolute URLs
// (issuer, registration_endpoint, etc.) emit the public https scheme rather than the
// internal http scheme the container sees.
//
// Trust model: the container is not directly reachable from the public internet — the
// Container Apps ingress is the only path in, and it overwrites/normalises the
// X-Forwarded-* headers from clients before forwarding. So we trust those headers
// implicitly via network isolation, not via authentication of the headers themselves.
// KnownNetworks/KnownProxies are cleared because we don't know the ACA ingress IP range
// statically. ForwardLimit=1 is defence-in-depth: it limits how many entries the
// middleware will process, preventing client-supplied chained headers from being honoured
// even if the ingress ever stops normalising them. If this app ever became reachable
// outside of an ingress (e.g. via private endpoint exposure), this configuration would
// need to tighten — either via KnownNetworks pinning or by removing ForwardedHeaders
// support entirely.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    // KnownIPNetworks replaces the legacy KnownNetworks per ASPDEPR005 (.NET 10).
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Fail-fast: force resolution of bound + PostConfigured options now so misconfiguration
// throws at startup rather than at first request.
_ = app.Services.GetRequiredService<IOptions<VitallyServerOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<OAuthOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<ToolAuthorizationOptions>>().Value;

app.UseForwardedHeaders();

// Unauthenticated liveness/readiness probe for Container Apps. Deliberately mapped before the
// auth middleware so platform health checks don't need a token.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

if (noAuth)
{
    app.Logger.LogWarning("Vitally MCP server running with NoAuth=true. This is for local development only — DO NOT use in production.");
}

// Prefer the configured canonical origin when set; otherwise derive from the request. The
// configured value defends against Host-header injection into the OAuth metadata documents.
static string GetServerBaseUrl(HttpContext ctx, string? publicBaseUrl)
{
    return string.IsNullOrWhiteSpace(publicBaseUrl)
        ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
        : publicBaseUrl;
}

// RFC 9728 — Protected Resource Metadata. Points clients at the authorization server,
// which for the DCR-proxy variant is *us* (so we can intercept registration). The actual
// token issuance still happens at Auth0 — our discovery doc points to Auth0's endpoints
// for everything except registration_endpoint.
app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx, IOptions<OAuthOptions> oauth) =>
{
    var o = oauth.Value;
    var resource = string.IsNullOrWhiteSpace(o.Resource) ? o.Audience : o.Resource;
    var asUrl = string.IsNullOrWhiteSpace(o.SharedClientId)
        ? o.Authority?.TrimEnd('/')
        : GetServerBaseUrl(ctx, o.PublicBaseUrl);
    return Results.Json(new
    {
        resource,
        authorization_servers = new[] { asUrl },
        bearer_methods_supported = new[] { "header" }
    });
});

// RFC 8414 — Authorization Server Metadata, served by us when the DCR proxy is enabled.
// Auth0 still issues tokens (its endpoints are what `authorization_endpoint`/`token_endpoint`
// point at), but `registration_endpoint` points at our own /oauth/register that hands every
// caller a pre-registered shared client_id (see POST /oauth/register below).
app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx, IOptions<OAuthOptions> oauth) =>
{
    var o = oauth.Value;
    if (string.IsNullOrWhiteSpace(o.SharedClientId))
    {
        // No proxy configured — return a 404 so clients fall back to the upstream AS metadata.
        return Results.NotFound();
    }
    var authority = (o.Authority ?? string.Empty).TrimEnd('/');
    var ourBase = GetServerBaseUrl(ctx, o.PublicBaseUrl);
    return Results.Json(new
    {
        issuer = authority + "/",
        authorization_endpoint = $"{ourBase}/oauth/authorize",
        token_endpoint = $"{ourBase}/oauth/token",
        userinfo_endpoint = $"{authority}/userinfo",
        jwks_uri = $"{authority}/.well-known/jwks.json",
        registration_endpoint = $"{ourBase}/oauth/register",
        scopes_supported = new[] { "openid", "profile", "email", "offline_access", "mcp.access" },
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        code_challenge_methods_supported = new[] { "S256" }
    });
});

// OAuth 2.0 Authorization Code proxy. The `Vitally MCP — Claude Code (shared)` Auth0 app
// has a single fixed callback URL (our /oauth/callback) — we accept any client redirect_uri
// here, save the mapping, replace with our fixed URL for the upstream Auth0 request, and
// at /oauth/callback look the original up and redirect there. This sidesteps Auth0's lack
// of RFC 8252 loopback wildcard support and lets random localhost ports + claude.ai's
// hosted callback URL coexist with one Auth0 app.
app.MapGet("/oauth/authorize", (HttpContext ctx, IOptions<OAuthOptions> oauth, IMemoryCache cache) =>
{
    var o = oauth.Value;
    if (string.IsNullOrWhiteSpace(o.SharedClientId))
    {
        return Results.Problem(detail: "OAuth proxy not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var query = ctx.Request.Query;
    var clientRedirectUri = query["redirect_uri"].ToString();
    var state = query["state"].ToString();

    if (string.IsNullOrWhiteSpace(clientRedirectUri) || string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "Missing redirect_uri or state" });
    }

    // Without this check, /oauth/callback would happily redirect victims to any attacker-
    // supplied URL with the authorisation code in the query string — and because we replace
    // the upstream redirect_uri with our own fixed callback, Auth0's own allowlist offers
    // no protection (every redirect_uri passes there). Loopback any-port is allowed per RFC
    // 8252 §7.3 (Claude Code, VS Code, Cursor, MCP Inspector); cloud-hosted MCP callbacks
    // must be listed in OAuth:AllowedClientRedirectUris.
    if (!o.IsRedirectUriAllowed(clientRedirectUri))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "redirect_uri is not allowed" });
    }

    cache.Set($"oauth-proxy:state:{state}", clientRedirectUri, TimeSpan.FromMinutes(10));

    var ourCallback = $"{GetServerBaseUrl(ctx, o.PublicBaseUrl)}/oauth/callback";
    var authority = (o.Authority ?? string.Empty).TrimEnd('/');
    var sb = new System.Text.StringBuilder($"{authority}/authorize?");
    foreach (var kv in query)
    {
        if (kv.Key == "redirect_uri") continue;
        // Strip `prompt` — some MCP clients send `prompt=consent` which forces Auth0 to
        // re-prompt every session even when a user_grant already exists. Without prompt=*
        // Auth0 honours the cached grant and silently issues an authorization code.
        if (kv.Key == "prompt") continue;
        foreach (var v in kv.Value)
        {
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(v ?? string.Empty)).Append('&');
        }
    }
    sb.Append("redirect_uri=").Append(Uri.EscapeDataString(ourCallback));
    return Results.Redirect(sb.ToString());
});

app.MapGet("/oauth/callback", (HttpContext ctx, IMemoryCache cache) =>
{
    var state = ctx.Request.Query["state"].ToString();
    if (string.IsNullOrWhiteSpace(state))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "Missing state" });
    }

    if (!cache.TryGetValue<string>($"oauth-proxy:state:{state}", out var clientRedirectUri) || string.IsNullOrWhiteSpace(clientRedirectUri))
    {
        return Results.BadRequest(new { error = "invalid_request", error_description = "Unknown or expired state" });
    }
    cache.Remove($"oauth-proxy:state:{state}");

    var separator = clientRedirectUri.Contains('?') ? "&" : "?";
    var sb = new System.Text.StringBuilder(clientRedirectUri).Append(separator);
    foreach (var kv in ctx.Request.Query)
    {
        foreach (var v in kv.Value)
        {
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(v ?? string.Empty)).Append('&');
        }
    }
    return Results.Redirect(sb.ToString().TrimEnd('&'));
});

app.MapPost("/oauth/token", async (HttpContext ctx, IOptions<OAuthOptions> oauth, IHttpClientFactory factory) =>
{
    var o = oauth.Value;
    var authority = (o.Authority ?? string.Empty).TrimEnd('/');
    var ourCallback = $"{GetServerBaseUrl(ctx, o.PublicBaseUrl)}/oauth/callback";

    var form = await ctx.Request.ReadFormAsync();
    var pairs = new List<KeyValuePair<string, string>>();
    var sawRedirect = false;
    foreach (var kv in form)
    {
        if (kv.Key == "redirect_uri")
        {
            pairs.Add(new KeyValuePair<string, string>("redirect_uri", ourCallback));
            sawRedirect = true;
        }
        else
        {
            foreach (var v in kv.Value)
            {
                pairs.Add(new KeyValuePair<string, string>(kv.Key, v ?? string.Empty));
            }
        }
    }
    // Restrict the grants this proxy will service. We inject a confidential client_secret
    // below, so we must never forward a grant the shared app shouldn't service — otherwise a
    // caller could request e.g. grant_type=client_credentials and receive a valid token for
    // our audience with no user sign-in, bypassing authentication entirely. The metadata
    // document advertises only these two grants (RFC 8414); enforce that here too.
    var grantType = pairs.FirstOrDefault(p => p.Key == "grant_type").Value;
    if (grantType is not ("authorization_code" or "refresh_token"))
    {
        return Results.BadRequest(new
        {
            error = "unsupported_grant_type",
            error_description = "This server only supports the authorization_code and refresh_token grants."
        });
    }

    // The redirect_uri parameter must match exactly what was sent in /authorize for code
    // exchange (per OAuth 2.0). Refresh-grant requests don't include it; only inject for code.
    if (grantType == "authorization_code" && !sawRedirect)
    {
        pairs.Add(new KeyValuePair<string, string>("redirect_uri", ourCallback));
    }

    // Confidential-client auth: inject the secret server-side. Clients (Claude Code etc.)
    // never see it — they post as if they were a public client, we add the secret on the way
    // upstream. This is what lets the shared Auth0 app be "verifiable first-party" and skip
    // the consent screen.
    if (!string.IsNullOrWhiteSpace(o.SharedClientSecret))
    {
        pairs.RemoveAll(p => p.Key == "client_secret");
        pairs.Add(new KeyValuePair<string, string>("client_secret", o.SharedClientSecret));
    }

    var client = factory.CreateClient();
    var upstream = await client.PostAsync($"{authority}/oauth/token", new FormUrlEncodedContent(pairs));
    var body = await upstream.Content.ReadAsStringAsync();
    return Results.Content(body, upstream.Content.Headers.ContentType?.MediaType ?? "application/json", System.Text.Encoding.UTF8, (int)upstream.StatusCode);
});

// RFC 7591 — Dynamic Client Registration intercept. We always return the same pre-registered
// shared client_id regardless of what the client requests. Echoes the requested redirect_uris
// so the client's local OAuth loop stays self-consistent.
app.MapPost("/oauth/register", async (HttpContext ctx, IOptions<OAuthOptions> oauth) =>
{
    var o = oauth.Value;
    if (string.IsNullOrWhiteSpace(o.SharedClientId))
    {
        return Results.Problem(
            detail: "DCR proxy not configured. Set OAuth:SharedClientId.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    string[] redirectUris = ["http://localhost"];
    try
    {
        ctx.Request.EnableBuffering();
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
        if (doc.RootElement.TryGetProperty("redirect_uris", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var uris = arr.EnumerateArray()
                .Select(x => x.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToArray();
            // Echo back only redirect_uris that the proxy would actually accept on /authorize.
            // Letting a non-allowed URI through here would mislead well-behaved clients into
            // configuring a redirect that we'd then reject at the authorize step.
            var accepted = uris.Where(o.IsRedirectUriAllowed).ToArray();
            if (accepted.Length == 0 && uris.Length > 0)
            {
                return Results.BadRequest(new
                {
                    error = "invalid_redirect_uri",
                    error_description = "None of the requested redirect_uris are permitted by this server."
                });
            }
            if (accepted.Length > 0) redirectUris = accepted;
        }
    }
    catch (JsonException)
    {
        // Tolerate clients that send empty or malformed bodies — they still get the static client_id back.
    }

    return Results.Json(new
    {
        client_id = o.SharedClientId,
        client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        redirect_uris = redirectUris,
        grant_types = new[] { "authorization_code", "refresh_token" },
        response_types = new[] { "code" },
        token_endpoint_auth_method = "none",
        application_type = "native"
    }, statusCode: StatusCodes.Status201Created);
});

if (!noAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var mcp = app.MapMcp("/mcp");
if (!noAuth)
{
    mcp.RequireAuthorization();
}

app.Run();

// Make Program accessible to integration tests in the test project.
public partial class Program { }
