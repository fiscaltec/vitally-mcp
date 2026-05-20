using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using VitallyMcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<VitallyServerOptions>()
    .Bind(builder.Configuration.GetSection(VitallyServerOptions.SectionName))
    .PostConfigure(o => o.Validate());

builder.Services.AddOptions<OAuthOptions>()
    .Bind(builder.Configuration.GetSection(OAuthOptions.SectionName));

builder.Services.AddMemoryCache();

var vitallySection = builder.Configuration.GetSection(VitallyServerOptions.SectionName);
var keyVaultUri = vitallySection["KeyVaultUri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Services.AddSingleton(new SecretClient(new Uri(keyVaultUri), new DefaultAzureCredential()));
}

builder.Services.AddScoped<VitallyApiKeyProvider>();
builder.Services.AddTransient<VitallyRateLimitHandler>();

builder.Services.AddHttpClient<VitallyService>()
    .AddHttpMessageHandler<VitallyRateLimitHandler>();

var oauthSection = builder.Configuration.GetSection(OAuthOptions.SectionName);
var noAuth = oauthSection.GetValue<bool>("NoAuth");

if (!noAuth)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = oauthSection["Authority"];
            options.Audience = oauthSection["Audience"];
        });
    builder.Services.AddAuthorization();
}

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

// Honour X-Forwarded-Proto / X-Forwarded-Host from Container Apps ingress so absolute URLs
// (issuer, registration_endpoint, etc.) emit the public https scheme rather than the
// internal http scheme the container sees.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedFor;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (noAuth)
{
    app.Logger.LogWarning("Vitally MCP server running with NoAuth=true. This is for local development only — DO NOT use in production.");
}

static string GetServerBaseUrl(HttpContext ctx)
{
    return $"{ctx.Request.Scheme}://{ctx.Request.Host}";
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
        : GetServerBaseUrl(ctx);
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
    var ourBase = GetServerBaseUrl(ctx);
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

    cache.Set($"oauth-proxy:state:{state}", clientRedirectUri, TimeSpan.FromMinutes(10));

    var ourCallback = $"{GetServerBaseUrl(ctx)}/oauth/callback";
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
    var ourCallback = $"{GetServerBaseUrl(ctx)}/oauth/callback";

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
    // The redirect_uri parameter must match exactly what was sent in /authorize for code
    // exchange (per OAuth 2.0). Refresh-grant requests don't include it; only inject for code.
    var grantType = pairs.FirstOrDefault(p => p.Key == "grant_type").Value;
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
            if (uris.Length > 0) redirectUris = uris;
        }
    }
    catch
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
