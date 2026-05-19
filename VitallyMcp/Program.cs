using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

var app = builder.Build();

if (noAuth)
{
    app.Logger.LogWarning("Vitally MCP server running with NoAuth=true. This is for local development only — DO NOT use in production.");
}
else
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/.well-known/oauth-protected-resource", (IOptions<OAuthOptions> oauth) =>
{
    var o = oauth.Value;
    var resource = string.IsNullOrWhiteSpace(o.Resource) ? o.Audience : o.Resource;
    return Results.Json(new
    {
        resource,
        authorization_servers = new[] { o.Authority?.TrimEnd('/') },
        bearer_methods_supported = new[] { "header" }
    });
});

var mcp = app.MapMcp("/mcp");
if (!noAuth)
{
    mcp.RequireAuthorization();
}

app.Run();

// Make Program accessible to integration tests in the test project.
public partial class Program { }
