using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
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

builder.Services.AddOptions<ToolsListCacheOptions>()
    .Bind(builder.Configuration.GetSection(ToolsListCacheOptions.SectionName))
    .PostConfigure(o => o.Validate());

builder.Services.AddMemoryCache();

// Needed so ToolAuthorizer can read the authenticated ClaimsPrincipal inside tool invocations.
builder.Services.AddHttpContextAccessor();

// Shared managed-identity credential (managed identity in prod, az login locally) used for both
// Key Vault and the Microsoft Graph group-membership lookup.
builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ => new DefaultAzureCredential());

// Live group-permission resolver (Microsoft Graph). Registered always; only invoked when
// Authorization:LiveGroupCheck is enabled. Short Graph timeout so a slow/unreachable Graph
// degrades to the token-claim fallback rather than stalling tool calls.
builder.Services.AddHttpClient<IGroupPermissionResolver, GraphGroupPermissionResolver>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(5);
});

// SecretClient registration uses the *validated* options (via IOptions) rather than raw
// config, so trimmed/URI-checked KeyVaultUri is what's constructed. Conditional on the
// raw config being present so the registration only fires when KV is actually configured.
var vitallySection = builder.Configuration.GetSection(VitallyServerOptions.SectionName);
if (!string.IsNullOrWhiteSpace(vitallySection["KeyVaultUri"]))
{
    builder.Services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<VitallyServerOptions>>().Value;
        return new SecretClient(new Uri(opts.KeyVaultUri!), sp.GetRequiredService<Azure.Core.TokenCredential>());
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

// Upstream endpoint resolution. Singleton so the last-known-good fallback outlives the cache TTL;
// its own named HttpClient so the discovery fetch cannot inherit another client's handlers, and so
// tests can substitute a stub handler for it alone.
builder.Services.AddHttpClient(UpstreamOidcMetadata.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<UpstreamOidcMetadata>();

// Fail fast on an unauthenticated production-looking config (NoAuth + Key Vault).
StartupGuards.EnsureSafeAuthConfig(noAuth, vitallySection["KeyVaultUri"]);

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
            // ValidAudiences rather than the single-valued jwt.Audience: which audience a provider
            // stamps on an access token is a property of the token version, not of our config. An
            // Entra v1 token names the App ID URI (OAuth:Audience); a v2 token names the resource
            // application's appId GUID — which, because one registration is both our OAuth client
            // and our API resource, is OAuth:SharedClientId. Accepting both settles it rather than
            // betting on it, and stays correct on Auth0, where SharedClientId is simply a second
            // audience nothing mints for this API. See OAuthOptions.ValidAudiences.
            jwt.TokenValidationParameters.ValidAudiences = oauth.Value.ValidAudiences;

            // MCP requires the 401 to point at the protected-resource metadata document. Without
            // this, clients can only guess the well-known location. Built from PublicBaseUrl so a
            // Host header cannot inject the pointer; falls back to the request origin in local dev.
            jwt.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    var baseUrl = GetServerBaseUrl(context.HttpContext, oauth.Value.PublicBaseUrl);

                    var metadataUrl = $"{baseUrl}{ProtectedResourceMetadataBuilder.MetadataPath}/mcp";

                    // Build a single WWW-Authenticate challenge and call HandleResponse() to stop
                    // JwtBearerHandler appending its own bare "Bearer" afterward — two challenges
                    // is HTTP-legal, but a client that reads only one of them (first or last) then
                    // sees no resource_metadata pointer at all, which is the exact discovery
                    // failure this task exists to close. Setting the status code ourselves is what
                    // makes suppressing the default handler safe here; ResourceMetadataDiscoveryTests
                    // pins the 401 independently, so the deploy.yml smoke contract stays covered.
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                    var challenge = $"Bearer resource_metadata=\"{metadataUrl}\"";
                    if (context.AuthenticateFailure is not null)
                    {
                        // A token was presented but failed validation — preserve that diagnostic
                        // (the default handler would otherwise add it) so expired/invalid-token
                        // issues stay debuggable in production. Never echo exception text or any
                        // token content into the header; the description stays generic.
                        challenge += ", error=\"invalid_token\", error_description=\"The access token is invalid or expired\"";
                    }
                    context.Response.Headers.WWWAuthenticate = challenge;

                    context.HandleResponse();
                    return Task.CompletedTask;
                }
            };
        });
}

// Registered unconditionally, including in NoAuth dev mode. The MCP SDK fails *closed*: with an
// authorisation attribute on a tool but no policy services (and no AddAuthorizationFilters() below),
// both tools/list and tools/call fail — observed as JSON-RPC -32603 "An error occurred." Since every
// tool carries [Authorize], guarding either of these on !noAuth leaves local development with a
// server that can neither list nor call anything. Verified both ways round; the regression is pinned
// by AuthorizationFilterToolsListTests.NoAuthDevMode_SeesEveryToolUnfiltered, which asserts the full
// tool count and so fails whichever way this breaks. Discovery is still unfiltered in dev mode —
// VitallyPermissionHandler short-circuits to success while
// ToolAuthorizer.IsAuthorizationBypassedAsync() is true (RBAC off or NoAuth).
//
// Scoped, not singleton: VitallyPermissionHandler depends on the scoped ToolAuthorizer, and a
// singleton capturing it would be a captive-dependency bug.
builder.Services.AddScoped<IAuthorizationHandler, VitallyPermissionHandler>();

// Policy *names* must be compile-time constants for [Authorize(Policy = "...")], so they are
// literals here. The permission *values* carried by each requirement come from
// ToolAuthorizationOptions, so a deployment that renames a permission stays consistent.
var permissions = builder.Configuration.GetSection(ToolAuthorizationOptions.SectionName)
    .Get<ToolAuthorizationOptions>() ?? new ToolAuthorizationOptions();
// Validate() must be called here, not just relied upon via the IOptions PostConfigure above.
// It trims the permission strings, and ToolAuthorizer receives the *validated* instance — so
// without this a config value like "vitally:read " (trailing space) would reach the [Authorize]
// policy requirement untrimmed while the SendAsync backstop compared against the trimmed form.
// Discovery filtering and enforcement would then disagree, which is the one thing sharing
// HasEffectivePermissionAsync is supposed to make impossible.
permissions.Validate();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("vitally:read", p => p.AddRequirements(new VitallyPermissionRequirement(permissions.ReadPermission)))
    .AddPolicy("vitally:write", p => p.AddRequirements(new VitallyPermissionRequirement(permissions.WritePermission)))
    .AddPolicy("vitally:delete", p => p.AddRequirements(new VitallyPermissionRequirement(permissions.DeletePermission)));

// Read the read-only flag from raw config at startup (same pattern as `noAuth` above) so the
// destructive tools can be filtered out of tools/list for read-only deployments.
var readOnlyMode = builder.Configuration.GetSection(ToolAuthorizationOptions.SectionName).GetValue<bool>("ReadOnly");

// Bound separately for the list-tools filter below, which is constructed at composition time.
var toolsListCache = builder.Configuration.GetSection(ToolsListCacheOptions.SectionName)
    .Get<ToolsListCacheOptions>() ?? new ToolsListCacheOptions();
toolsListCache.Validate();

var mcpBuilder = builder.Services.AddMcpServer(options => options.ServerInstructions = VitallyServerInstructions.Text)
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

// Applies [Authorize] on tools: filters tools/list per caller and rejects unauthorised calls
// before they reach the handler. Not guarded on !noAuth — see the AddAuthorizationBuilder comment
// above: with authorisation metadata on a tool and this call missing, tools/list fails outright, so
// omitting it in dev mode breaks local development. NoAuth is handled inside the policy handler.
//
// The checkpoint this installs sits *outside* the whole call-tool filter pipeline (the SDK's
// "alternate-result pipeline"), so a refused call never reaches the filters registered below —
// including the error-surfacing one. That is why the denial is audited from
// VitallyPermissionHandler rather than from a filter here.
mcpBuilder.AddAuthorizationFilters();

mcpBuilder.WithRequestFilters(filters =>
{
    // Surface the real failure reason (Vitally body / read-only / RBAC denial / validation) to the
    // client instead of the SDK's generic "An error occurred invoking 'X'." Unexpected exceptions
    // propagate so the SDK keeps its protocol-error / cancellation handling and generic message.
    filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception ex) when (ToolErrorResult.IsSurfaceable(ex))
        {
            return ToolErrorResult.Build(ex);
        }
    });

    // Advertise how long clients may cache tools/list (2026-07-28 spec). Private scope because
    // the list is per-caller once authorisation filtering is on.
    if (toolsListCache.Enabled)
    {
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.TimeToLive = toolsListCache.TimeToLive;
            result.CacheScope = toolsListCache.Scope;
            return result;
        });
    }

    // Read-only deployments: hide destructive tools from tools/list (enforcement is in ToolAuthorizer).
    if (readOnlyMode)
    {
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = ReadOnlyToolFilter.FilterTools(result.Tools, readOnly: true);
            return result;
        });
    }
});

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
var oauthOptions = app.Services.GetRequiredService<IOptions<OAuthOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<ToolAuthorizationOptions>>().Value;

// Same fail-fast intent, one step further out: prove the upstream provider actually publishes the
// four endpoints the proxy needs before serving anything. Blocking here is the point — two of them
// go into a metadata document clients trust. See StartupGuards for why it is fatal.
//
// `SharedClientId` is read from the resolved options rather than from `oauthSection` alongside
// `noAuth` above, and the difference is load-bearing: that composition-time read happens before
// WebApplicationFactory injects its configuration, so a raw read here would report the proxy
// disabled in every integration test and quietly skip the guard the tests exist to pin.
await StartupGuards.EnsureUpstreamOidcEndpointsAsync(
    app.Services.GetRequiredService<UpstreamOidcMetadata>(),
    proxyEnabled: !string.IsNullOrWhiteSpace(oauthOptions.SharedClientId),
    timeout: TimeSpan.FromSeconds(15));

app.UseForwardedHeaders();

// Unauthenticated liveness/readiness probe for Container Apps. This endpoint carries no
// RequireAuthorization() (and the MCP endpoint's auth requirement doesn't apply to it), so
// platform health checks reach it without a token.
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

// RFC 9728 — Protected Resource Metadata, served from both the canonical path and the
// resource-path-suffixed variant (…/mcp) that RFC 9728 and the MCP SDK prefer. Clients probe
// either, so serving both removes a discovery failure mode. Points clients at the authorization
// server, which for the DCR-proxy variant is *us* (so we can intercept registration). The actual
// token issuance still happens at Auth0 — our discovery doc points to Auth0's endpoints for
// everything except registration_endpoint.
// Serialised with the SDK's own options rather than the ASP.NET Core defaults, because those
// write every unset optional property as an explicit `null`. RFC 9728 §3.2 says an unused
// metadata parameter is *omitted*, and strict clients enforce the difference: the published
// @modelcontextprotocol/client schema types `jwks_uri` as a string and rejects the whole
// document on a null, before any part of the OAuth flow is reached.
var resourceMetadataHandler = (HttpContext ctx, IOptions<OAuthOptions> oauth) =>
    Results.Json(
        ProtectedResourceMetadataBuilder.Build(oauth.Value, GetServerBaseUrl(ctx, oauth.Value.PublicBaseUrl)),
        McpJsonUtilities.DefaultOptions);

app.MapGet(ProtectedResourceMetadataBuilder.MetadataPath, resourceMetadataHandler);
app.MapGet($"{ProtectedResourceMetadataBuilder.MetadataPath}/mcp", resourceMetadataHandler);

// RFC 8414 — Authorization Server Metadata, served by us when the DCR proxy is enabled.
// `issuer` names our *own* origin, not Auth0's. §3.3 requires the issuer to correspond to the URL
// the document was fetched from (an anti-mix-up control), and from the client's point of view we
// genuinely are the authorization server: authorize, token and register are all ours. Auth0 still
// issues the tokens, which is why `jwks_uri` and `userinfo_endpoint` remain upstream — and why they
// are read from the provider's own discovery document rather than assembled from Authority, which
// only ever produced Auth0-shaped paths. Declaring our origin here is coupled to the `iss` injection
// in /oauth/callback below — see the façade section in CLAUDE.md before changing either.
app.MapGet("/.well-known/oauth-authorization-server", async (HttpContext ctx, IOptions<OAuthOptions> oauth, UpstreamOidcMetadata upstream) =>
{
    var o = oauth.Value;
    if (string.IsNullOrWhiteSpace(o.SharedClientId))
    {
        // No proxy configured — return a 404 so clients fall back to the upstream AS metadata.
        return Results.NotFound();
    }
    var endpoints = await upstream.GetAsync(ctx.RequestAborted);
    var ourBase = GetServerBaseUrl(ctx, o.PublicBaseUrl);
    return Results.Json(new
    {
        issuer = ourBase,
        authorization_endpoint = $"{ourBase}/oauth/authorize",
        token_endpoint = $"{ourBase}/oauth/token",
        userinfo_endpoint = endpoints.UserInfoEndpoint,
        jwks_uri = endpoints.JwksUri,
        registration_endpoint = $"{ourBase}/oauth/register",
        // Shared with the RFC 9728 document rather than restated, so the two lists cannot drift.
        // The API scope is named in the form the upstream provider will actually accept — see
        // ProtectedResourceMetadataBuilder.ScopesFor.
        scopes_supported = ProtectedResourceMetadataBuilder.ScopesFor(o),
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        code_challenge_methods_supported = new[] { "S256" },
        // RFC 9207. Honest only because /oauth/callback injects `iss` unconditionally — a client
        // that sees this flag and then no `iss` on the authorization response treats the absence
        // as a stripped-parameter attack and aborts, so the two must ship together.
        authorization_response_iss_parameter_supported = true
    });
});

// OAuth 2.0 Authorization Code proxy. The `Vitally MCP — Claude Code (shared)` Auth0 app
// has a single fixed callback URL (our /oauth/callback) — we accept any client redirect_uri
// here, save the mapping, replace with our fixed URL for the upstream Auth0 request, and
// at /oauth/callback look the original up and redirect there. This sidesteps Auth0's lack
// of RFC 8252 loopback wildcard support and lets random localhost ports + claude.ai's
// hosted callback URL coexist with one Auth0 app.
app.MapGet("/oauth/authorize", async (HttpContext ctx, IOptions<OAuthOptions> oauth, IMemoryCache cache, UpstreamOidcMetadata upstream) =>
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

    // RFC 8707. `resource` is what binds the audience of the token that comes back — this proxy
    // sends no `audience` parameter anywhere — so a client must not be able to ask to be bound to
    // an audience this server never published. Refuse a mismatch regardless of what happens to the
    // value afterwards, because the check is about what we are willing to request, not about which
    // provider consumes it.
    //
    // What happens afterwards is OAuth:UpstreamResourceScope's job. Empty (Auth0): the value is
    // relayed verbatim, because the tenant's Resource Parameter Compatibility Profile consuming it
    // is the only thing binding the audience there. Set (Entra): it is dropped here and the
    // configured scope carries the same meaning instead, because Entra's v2 authorize endpoint
    // refuses *any* `resource` alongside a custom-API `scope` — AADSTS9010010, "the resource
    // parameter provided in the request doesn't match with the requested scopes". Verified against
    // the live tenant on 2026-09-02: the slashed form, the exact App ID URI and an unregistered
    // value all return 400 alike. It is a resource-vs-scope consistency check, not a comparison
    // against identifierUris, so no amount of reshaping the value would have worked.
    //
    // Indexer lookups on IQueryCollection are case-insensitive, so an oddly-cased `RESOURCE` is
    // validated here too — and refused if it does not match. When the parameter is terminated the
    // forwarding loop drops it case-insensitively as well; when it is relayed, the loop preserves
    // the caller's key casing and such a pair travels upstream as-is, which a conformant provider
    // ignores because RFC 6749 defines parameter names as lowercase literals. #123 tracks stripping
    // those rather than relying on it.
    if (query.ContainsKey("resource")
        && query["resource"].Any(value => !o.IsResourceIndicatorAllowed(value ?? string.Empty)))
    {
        return Results.BadRequest(new
        {
            error = "invalid_target",
            error_description = "The resource parameter does not match the resource identifier this server publishes."
        });
    }

    cache.Set($"oauth-proxy:state:{state}", clientRedirectUri, TimeSpan.FromMinutes(10));

    var ourCallback = $"{GetServerBaseUrl(ctx, o.PublicBaseUrl)}/oauth/callback";
    var endpoints = await upstream.GetAsync(ctx.RequestAborted);
    // Repeated `scope` is not legal OAuth, but a client that sends it means the union — so join on
    // a space rather than letting StringValues.ToString() comma-splice the values into one token.
    var mergedScope = o.TerminatesResourceParameter
        ? o.MergeUpstreamScope(string.Join(' ', query["scope"].Select(v => v ?? string.Empty)))
        : null;
    // The provider is free to publish an authorization_endpoint that already carries a query
    // (nothing in OIDC Discovery forbids it), so pick the separator rather than assuming '?'.
    var separator = endpoints.AuthorizationEndpoint.Contains('?') ? '&' : '?';
    var sb = new System.Text.StringBuilder(endpoints.AuthorizationEndpoint).Append(separator);
    foreach (var kv in query)
    {
        if (kv.Key == "redirect_uri") continue;
        // Strip `prompt` — some MCP clients send `prompt=consent` which forces Auth0 to
        // re-prompt every session even when a user_grant already exists. Without prompt=*
        // Auth0 honours the cached grant and silently issues an authorization code.
        if (kv.Key == "prompt") continue;
        if (mergedScope is not null
            && (string.Equals(kv.Key, "resource", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Key, "scope", StringComparison.OrdinalIgnoreCase)))
        {
            // Terminated here (`resource`) or re-emitted once below in merged form (`scope`).
            // OrdinalIgnoreCase and not ==: a `RESOURCE` that survived would defeat the whole point
            // of dropping it, and letting a `SCOPE` through would put two scope parameters upstream.
            continue;
        }
        foreach (var v in kv.Value)
        {
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(v ?? string.Empty)).Append('&');
        }
    }
    if (mergedScope is not null)
    {
        sb.Append("scope=").Append(Uri.EscapeDataString(mergedScope)).Append('&');
    }
    sb.Append("redirect_uri=").Append(Uri.EscapeDataString(ourCallback));
    return Results.Redirect(sb.ToString());
});

app.MapGet("/oauth/callback", (HttpContext ctx, IOptions<OAuthOptions> oauth, IMemoryCache cache) =>
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
        // Drop any upstream `iss` — ours is appended below. Auth0 sends one naming itself when the
        // tenant is configured for RFC 9207, and whether it does is tenant configuration we don't
        // control. Forwarding it would contradict the issuer we publish; appending ours alongside
        // would leave the client two values to choose between. Clients compare a *present* `iss`
        // against the metadata issuer even when the parameter is not advertised, so both shapes
        // fail rather than degrade.
        // OrdinalIgnoreCase, not ==: IQueryCollection lookups are case-insensitive but enumeration
        // yields keys as they were parsed, so an exact match would forward a differently-cased
        // `ISS` alongside the one we append below.
        if (string.Equals(kv.Key, "iss", StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var v in kv.Value)
        {
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(v ?? string.Empty)).Append('&');
        }
    }
    // RFC 9207 §2 — name ourselves as the issuer of this authorization response. Must match the
    // `issuer` in our RFC 8414 document byte for byte: the comparison is simple string equality,
    // with no scheme/host case folding, trailing-slash or percent-encoding normalisation applied.
    sb.Append("iss=").Append(Uri.EscapeDataString(GetServerBaseUrl(ctx, oauth.Value.PublicBaseUrl)));
    return Results.Redirect(sb.ToString().TrimEnd('&'));
});

app.MapPost("/oauth/token", async (HttpContext ctx, IOptions<OAuthOptions> oauth, IHttpClientFactory factory, UpstreamOidcMetadata upstream) =>
{
    var o = oauth.Value;
    var ourCallback = $"{GetServerBaseUrl(ctx, o.PublicBaseUrl)}/oauth/callback";

    var form = await ctx.Request.ReadFormAsync();
    // Same audience-binding control as /oauth/authorize, applied to the form body — a refresh
    // exchange can carry `resource` just as a code exchange can. See the comment there for what
    // OAuth:UpstreamResourceScope then does with a value that passes.
    if (form.ContainsKey("resource")
        && form["resource"].Any(value => !o.IsResourceIndicatorAllowed(value ?? string.Empty)))
    {
        return Results.BadRequest(new
        {
            error = "invalid_target",
            error_description = "The resource parameter does not match the resource identifier this server publishes."
        });
    }

    // Merged for both grants, not just refresh. On a refresh exchange it is load-bearing: Entra
    // issues the new access token for whatever resource `scope` names, so a client that refreshes
    // with only the OIDC scopes would silently be handed a token for the wrong audience. On a code
    // exchange it is redundant but harmless — the scope was already consented at /oauth/authorize —
    // and applying one rule to both is easier to keep true than a per-grant one.
    var mergedScope = o.TerminatesResourceParameter
        ? o.MergeUpstreamScope(string.Join(' ', form["scope"].Select(v => v ?? string.Empty)))
        : null;

    var pairs = new List<KeyValuePair<string, string>>();
    var sawRedirect = false;
    foreach (var kv in form)
    {
        if (kv.Key == "redirect_uri")
        {
            pairs.Add(new KeyValuePair<string, string>("redirect_uri", ourCallback));
            sawRedirect = true;
        }
        else if (mergedScope is not null
            && (string.Equals(kv.Key, "resource", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Key, "scope", StringComparison.OrdinalIgnoreCase)))
        {
            // Terminated (`resource`) or replaced by the merged value below (`scope`) — see the
            // matching skip in /oauth/authorize.
            continue;
        }
        else
        {
            foreach (var v in kv.Value)
            {
                pairs.Add(new KeyValuePair<string, string>(kv.Key, v ?? string.Empty));
            }
        }
    }
    if (mergedScope is not null)
    {
        pairs.Add(new KeyValuePair<string, string>("scope", mergedScope));
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
        // Never pair the injected secret with a caller-supplied client_id — clamp to our shared app.
        pairs.RemoveAll(p => p.Key == "client_id");
        pairs.Add(new KeyValuePair<string, string>("client_id", o.SharedClientId));
    }

    var endpoints = await upstream.GetAsync(ctx.RequestAborted);
    var client = factory.CreateClient();
    using var upstreamResponse = await client.PostAsync(endpoints.TokenEndpoint, new FormUrlEncodedContent(pairs), ctx.RequestAborted);
    var body = await upstreamResponse.Content.ReadAsStringAsync(ctx.RequestAborted);
    return Results.Content(body, upstreamResponse.Content.Headers.ContentType?.MediaType ?? "application/json", System.Text.Encoding.UTF8, (int)upstreamResponse.StatusCode);
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
