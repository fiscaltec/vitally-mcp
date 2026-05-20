using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
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
        authorization_endpoint = $"{authority}/authorize",
        token_endpoint = $"{authority}/oauth/token",
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
