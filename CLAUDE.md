# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Model Context Protocol (MCP) server implementation in C# that provides full CRUD access to the Vitally customer success platform. The server is a **remote HTTP MCP server** secured with Auth0 (which federates to Microsoft Entra for FISCAL identity); users connect to it by URL rather than installing a binary.

**Key characteristics:**
- Full CRUD API access to Vitally resources (accounts, organisations, users, conversations, notes, projects, tasks, admins, NPS responses, project templates, project categories, messages, custom objects, meetings — including participants and transcripts — custom traits, custom surveys)
- Permission management via `ReadOnly` and `Destructive` flags on every tool, for MCP clients to enforce per-category permissions
- **Streamable HTTP transport** (MCP 2025-06-18) on the `ModelContextProtocol.AspNetCore` package, stateless mode
- **Auth0 OAuth 2.1 protection** via JwtBearer on `/mcp`; publishes RFC 9728 protected-resource metadata at `/.well-known/oauth-protected-resource`. An in-process OAuth proxy fronts the upstream Auth0 tenant when `OAuth:SharedClientId` is set — it implements an RFC 7591 DCR shim so every MCP client converges on one pre-registered first-party app (skipping the per-session consent screen and accepting any RFC 8252 loopback port). Non-loopback `redirect_uri` values must be in `OAuth:AllowedClientRedirectUris`. **Auth0 tenant must have "Resource Parameter Compatibility Profile" enabled** (Settings → Advanced) so the `resource` parameter from MCP clients is consumed locally and not forwarded to upstream IdPs (avoids AADSTS9010010 with the Entra federation hop).
- **On-demand Vitally API key fetch**: the server fetches the `vitally-shared` secret from Azure Key Vault via its user-assigned managed identity (with a short in-memory cache) and uses it to call Vitally on behalf of all authenticated users. Future per-user keys can be added by reintroducing claim-based secret resolution.
- .NET 10 ASP.NET Core, framework-dependent — runs in any .NET 10 container
- Built on the official `ModelContextProtocol` C# SDK (1.3.0 GA) + `ModelContextProtocol.AspNetCore`
- Multi-region support: EU (default, `rest.vitally-eu.io`) and US (`{subdomain}.rest.vitally.io`)
- Rate-limit-aware HTTP pipeline: auto-retries on `429 Too Many Requests` and logs a warning when `X-RateLimit-Remaining` drops below threshold

## Common Development Commands

### Build, test, run

```powershell
# Restore + build + run the test suite
dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal

# Build only (Debug)
dotnet build VitallyMcp.sln

# Run a single test class
dotnet test --filter "FullyQualifiedName~MeetingsToolsTests"

# Start the server in dev mode (no Auth0, no Key Vault)
$env:OAuth__NoAuth = "true"
$env:Vitally__Region = "EU"
$env:Vitally__DevelopmentApiKey = "sk_live_your_key"
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project VitallyMcp/VitallyMcp.csproj
```

### Smoke-testing the server

With the server running locally (or against the deployed URL with a real JWT in the `Authorization` header):

```powershell
# OAuth metadata document — clients use this to discover the auth server
Invoke-RestMethod http://localhost:5099/.well-known/oauth-protected-resource

# MCP initialise
$body = @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-06-18'; capabilities=@{}; clientInfo=@{ name='smoke'; version='0.0.1' } } } | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body

# tools/list
$body = @{ jsonrpc='2.0'; id=2; method='tools/list' } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body
```

## Installing for End Users

FISCAL employees point their MCP client at `https://vitally.fiscaltec.com/mcp`. The client handles the Auth0 OAuth flow automatically on first use via the protected-resource metadata document (Auth0 federates to Entra for the actual sign-in).

| Client | How to connect |
|---|---|
| Claude Desktop | Settings → Connectors → Add custom connector → paste the URL |
| Claude Code | `claude mcp add --transport http vitally https://vitally.fiscaltec.com/mcp` |
| VS Code / Cursor / other | Add an MCP server entry pointing at the URL; client handles OAuth |

To update: nothing for end users. The server is the source of truth; new deploys ship automatically.

## Architecture

### Hosting and transport (Program.cs)

The server uses ASP.NET Core 10 with `WebApplication.CreateBuilder` + `Microsoft.NET.Sdk.Web`. Key wiring:

- Binds `VitallyServerOptions` from the `Vitally:` configuration section, calls `Validate()` at startup to fail-fast on bad config.
- Binds `OAuthOptions` from `OAuth:` (provider-agnostic — works with Auth0, Entra direct, Keycloak, etc.).
- Conditionally registers `SecretClient` (Azure Key Vault) as singleton when `Vitally:KeyVaultUri` is set; uses `DefaultAzureCredential` so it works with managed identity in production and `az login` locally.
- `IMemoryCache` registered for the API key cache and OAuth proxy state cache.
- `VitallyApiKeyProvider` registered scoped.
- `VitallyRateLimitHandler` registered transient and attached to the `HttpClient` used by `VitallyService`.
- `JwtBearer` authentication added unless `OAuth:NoAuth=true`.
- `ForwardedHeadersOptions` configured to honour `X-Forwarded-Proto` / `X-Forwarded-Host` / `X-Forwarded-For` from the Container Apps ingress (trust model: network isolation, not header authentication — see comments in `Program.cs`).
- MCP server registered via `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithToolsFromAssembly()`.
- OAuth proxy endpoints (only active when `OAuth:SharedClientId` is set):
  - `GET /.well-known/oauth-protected-resource` — RFC 9728 metadata.
  - `GET /.well-known/oauth-authorization-server` — RFC 8414 metadata pointing `registration_endpoint` at our DCR shim.
  - `GET /oauth/authorize` — validates the client `redirect_uri` against `OAuth:AllowedClientRedirectUris` (plus implicit loopback exemption), stashes it keyed by `state`, and proxies to Auth0 with our own fixed callback.
  - `GET /oauth/callback` — reverses the stash, redirects to the original client URI.
  - `POST /oauth/token` — proxies code/refresh exchanges to Auth0, server-side-injecting `SharedClientSecret`.
  - `POST /oauth/register` — RFC 7591 DCR shim returning `SharedClientId` plus filtered `redirect_uris`.
- `MapMcp("/mcp").RequireAuthorization()` — auth requirement is dropped when `NoAuth=true`.

### Configuration (VitallyServerOptions.cs + OAuthOptions.cs)

`VitallyServerOptions` (singleton, bound from `Vitally:` section):
- `Region` — `EU` (default) or `US`. Validated at startup.
- `Subdomain` — required only when `Region=US`.
- `KeyVaultUri` — Azure Key Vault URI. When unset, the server requires `DevelopmentApiKey` instead (local dev only).
- `DefaultSecretRef` — Key Vault secret name to fetch (default `vitally-shared`).
- `SecretCacheDuration` — TTL for the in-memory API key cache (default 5 min).
- `DevelopmentApiKey` — local-only fallback used when `KeyVaultUri` is unset.
- `BaseUrl` — computed: EU → `https://rest.vitally-eu.io`; US → `https://{Subdomain}.rest.vitally.io`.

`OAuthOptions` (singleton, bound from `OAuth:` section):
- `Authority` — OAuth/OIDC issuer URL with trailing slash, e.g. `https://fiscal-it.uk.auth0.com/`.
- `Audience` — the Auth0 Resource Server / API identifier, e.g. `https://vitally.fiscaltec.com`. Validated against the JWT `aud` claim.
- `Resource` — canonical resource identifier published in `/.well-known/oauth-protected-resource` (falls back to `Audience` if empty). Set explicitly when MCP clients validate metadata `resource` against the server URL/origin (RFC 8707 + RFC 9728 compliance).
- `SharedClientId` — pre-registered Auth0 native-app client_id that every MCP client converges on via the DCR shim. When set, the OAuth proxy endpoints become active.
- `SharedClientSecret` — confidential-client secret for `SharedClientId`, injected server-side at `/oauth/token`.
- `AllowedClientRedirectUris` — non-loopback `redirect_uri` allowlist for the OAuth proxy. Loopback URIs (`localhost`, `127.0.0.1`, `[::1]`) on any port are always allowed per RFC 8252 §7.3; this list covers hosted MCP clients like `https://claude.ai/api/mcp/auth_callback`. `OAuthOptions.IsRedirectUriAllowed(uri)` is the single check; `/oauth/authorize` and `/oauth/register` both use it. **This is the only thing standing between the proxy and an open redirector with authorisation-code theft — never bypass it.**
- `PublicBaseUrl` — canonical public origin (e.g. `https://vitally.fiscaltec.com`). When set, `/.well-known/*` metadata and the OAuth proxy callback are built from this instead of the request `Host`, defending against Host-header injection into the metadata documents. Empty in local dev (falls back to request scheme+host so loopback works). Validated as absolute https.
- `NoAuth` — local-only dev flag that bypasses JWT validation entirely.

`ToolAuthorizationOptions` (singleton, bound from `Authorization:` section):
- `Enabled` (default `true`), `ReadPermission` (`vitally:read`), `WritePermission` (`vitally:write`), `DeletePermission` (`vitally:delete`), `CustomPermissionsClaim` (default `https://vitally.fiscaltec.com/permissions`).
- `LiveGroupCheck` (default `false`), `LiveGroupCacheSeconds` (default `60`), `ReaderGroupId`/`EditorGroupId`/`AdminGroupId` (Entra group object ids).
- Permissions are read from the JWT `permissions` claim (Auth0 RBAC), the namespaced `CustomPermissionsClaim` (for the Entra-group→Action→custom-claim assignment model), or space-delimited `scope`. The Entra-group-driven model is the chosen assignment approach: a post-login Auth0 Action maps Entra group membership to the `vitally:*` permissions and writes them to the custom claim.
- **Live group check (preferred for prompt propagation):** when `LiveGroupCheck=true`, `ToolAuthorizer` resolves permissions from the caller's *current* Entra group membership via `GraphGroupPermissionResolver` (Microsoft Graph `checkMemberGroups`, using the managed identity, cached `LiveGroupCacheSeconds` per user) instead of the token claim — so grants and **revocations** take effect within the cache window regardless of token/refresh age (the post-login Action does **not** re-run on refresh grants, so the claim alone is frozen at login). The object id is taken from the `oid` claim or the trailing GUID of `sub`. The token claim is the automatic fallback if the Graph lookup fails (fail-degraded). Requires the managed identity to hold Graph `GroupMember.Read.All`.
- Server-side RBAC backstop. `ToolAuthorizer.EnsureAuthorizedAsync(method, ct)` is awaited from **`VitallyService.SendAsync`** — the single point every Vitally call funnels through — so all ~92 tools are covered without per-tool annotation. The HTTP verb maps to the tier: GET → read, POST/PUT/PATCH → write, DELETE → delete (unknown verbs fall back to the strictest). Permissions are read from the JWT `permissions` claim (Auth0 RBAC), the namespaced `CustomPermissionsClaim`, or space-delimited `scope` (same three sources as above). Bypassed when `Enabled=false` or `OAuth:NoAuth=true`. **The `ReadOnly`/`Destructive` tool attributes are advisory client hints; this is the actual enforcement — when adding a new call path, route it through `VitallyService.SendAsync` so it stays covered, and never call the Vitally API around it.**

`AuditOptions` (singleton, bound from `Audit:` section):
- `Enabled` (default `true`), `IncludeReads` (default `false`).
- `AuditLogger` is invoked from **`VitallyService.SendAsync`** (same choke point): `LogAction` after each upstream response and `LogDenied` on an RBAC denial. Records the authenticated user's stable subject id (`sub` claim, falling back to NameIdentifier, else `anonymous`), HTTP verb, resource path (query string stripped) and status code via structured logging — so the named properties become queryable dimensions in Application Insights / Log Analytics. **Log the `sub` (opaque, attributable Entra object id), never the email — keep personal data out of telemetry. Never log request/response bodies here either — they can contain customer PII (traits, transcripts).** This is the attribution mechanism while a single shared Vitally key is in use (per-user keys via the `secret_ref` claim remain a future option).

### API key resolution (VitallyApiKeyProvider.cs)

Scoped. Resolution order on each call to `GetApiKeyAsync()`:

1. If no `SecretClient` is registered (i.e. `KeyVaultUri` not set) and `DevelopmentApiKey` is set, return it. If neither is set, throw.
2. Check `IMemoryCache` for `"vitally-api-key::{DefaultSecretRef}"`. Return if hit.
3. Call `SecretClient.GetSecretAsync(DefaultSecretRef)` (uses the Container App's user-assigned managed identity), cache the value for `SecretCacheDuration`, return.

This means: rotating the Vitally key is a `Set-AzKeyVaultSecret` away (cache expires on its own). Per-user keys can be re-introduced later by extending the provider to read the `https://vitally.fiscaltec.com/secret_ref` claim (set by the Auth0 Action) and selecting a different secret name per user — no other architecture changes needed.

### HTTP Service (VitallyService.cs)

Scoped via `AddHttpClient<VitallyService>()`. Per-request auth: the constructor takes the per-request `VitallyApiKeyProvider`, and the private `SendAsync(method, url, content?)` helper builds each `HttpRequestMessage`, fetches the API key from the provider, sets the `Authorization: Basic` header on the message, and dispatches via `_httpClient.SendAsync`. The shared `HttpClient` is *not* mutated — there's no `DefaultRequestHeaders.Authorization`, so multi-user safety is preserved.

On non-2xx responses `SendAsync` reads the response body, disposes the response, and throws `HttpRequestException` with `StatusCode` set and a message that includes a truncated copy of the response body. This deliberately replaces `EnsureSuccessStatusCode()` because Vitally returns the actual failure reason (e.g. `{"message":"externalId is required"}`) in the body, and surfacing it gives the LLM something concrete to act on.

Standard methods (apply field/trait filtering and the `{results, next}` envelope):
- `GetResourcesAsync` — list with pagination, sorting, filtering
- `GetResourceByIdAsync`
- `CreateResourceAsync` / `UpdateResourceAsync` / `DeleteResourceAsync`

Raw pass-through methods (no field filtering — for endpoints whose response shape is not the standard `{results, next}` envelope, e.g. surveys returning `{data, next}`, customFields returning a bare array, or for sub-resource paths like meeting participants):
- `GetRawAsync(path, queryParams)` — GET with URL-encoded query string
- `PostRawAsync(path, jsonBody)`
- `DeleteRawAsync(path)`

### Rate-Limit Handler (VitallyRateLimitHandler.cs)

Vitally's documented limit is **1000 requests / minute (sliding window)**. The handler is a `DelegatingHandler` registered via `AddHttpMessageHandler<VitallyRateLimitHandler>()` in `Program.cs`, so all HTTP calls made by `VitallyService` go through it transparently.

Behaviour:
- **On HTTP 429 Too Many Requests:** waits and retries up to `MaxRetries` (default 3). Wait time is taken from `Retry-After` (preferred), then `X-RateLimit-Reset` (Unix seconds), falling back to `FallbackRetryDelay` (default 5s). The wait is capped at `MaxRetryDelay` (default 60s).
- **On any non-429 response:** if `X-RateLimit-Remaining` is below `LowRemainingThreshold` (default 50), logs a warning via `ILogger` so callers can throttle themselves.
- **When retries are exhausted:** the 429 response is returned to the caller, which propagates as `HttpRequestException` via `EnsureSuccessStatusCode`.

All thresholds are public mutable properties, so they can be tweaked in tests or future configuration without touching the handler internals.

**Vitally API Parameters:**
- Pagination uses `from` parameter (not `cursor`) - pass the `next` value from previous response
- Sorting via `sortBy` parameter: `"createdAt"` or `"updatedAt"` (default: updatedAt)
- Resource-specific filters (e.g., `status` for accounts: active, churned, activeOrChurned)

**Client-Side Filtering:**
- The Vitally API does NOT support field or trait selection natively
- `VitallyService` implements client-side JSON filtering after receiving full API response
- Uses `System.Text.Json.JsonDocument` to parse and filter fields and traits
- Only includes fields that actually exist on the resource (via `TryGetProperty`)
- Preserves pagination metadata (`next` field) in filtered responses
- **Trait filtering:** When traits parameter is specified, filters the traits object to include only requested trait keys
- **Default behaviour:** Traits are excluded by default to reduce response size - use traits parameter to include specific traits
- Reduces response size before returning to LLM

**Resource-Specific Default Fields:**

When no fields are specified, each resource type returns an optimised field set:

| Resource | Default Fields |
|----------|----------------|
| **Accounts** | id, name, createdAt, updatedAt, externalId, organizationId, healthScore, mrr, accountOwnerId, lastSeenTimestamp |
| **Organizations** | id, name, createdAt, updatedAt, externalId, healthScore, mrr, lastSeenTimestamp |
| **Users** | id, name, createdAt, updatedAt, externalId, email, accountId, organizationId, lastSeenTimestamp |
| **Conversations** | id, externalId, subject, authorId, accountId, organizationId |
| **Notes** | id, createdAt, updatedAt, externalId, subject, noteDate, authorId, accountId, organizationId, categoryId, archivedAt |
| **Tasks** | id, name, createdAt, updatedAt, externalId, dueDate, completedAt, assignedToId, accountId, organizationId, archivedAt |
| **Projects** | id, name, createdAt, updatedAt, accountId, organizationId, archivedAt |
| **Admins** | id, name, email |
| **NPS Responses** | id, externalId, userId, score, feedback, respondedAt |
| **Project Templates** | id, name, createdAt, updatedAt, projectCategoryId, description |
| **Project Categories** | id, name, createdAt, updatedAt |
| **Messages** | id, type, externalId, timestamp, message, from, to |
| **Custom Objects** | id, name, createdAt, updatedAt |
| **Note Categories** | id, name, createdAt, updatedAt |
| **Task Categories** | id, name, createdAt, updatedAt |
| **Meetings** | id, title, externalId, startDateTime, endDateTime, location, source, accountIds, organizationIds, participants, createdAt, updatedAt |
| **Meeting Transcripts** | id, meetingId, createdAt, updatedAt |
| **Custom Object Instances** | id, name, externalId, createdAt, updatedAt, organizationId, customerId, archivedAt |
| **Admins / Admins Search** | id, name, email |

These defaults balance usefulness (business context, relationships, key metrics) with response size (excluding large fields like traits objects, rich text content, transcript bodies, and meeting summaries).

**Resources NOT using field filtering** (raw pass-through):
- **Custom Traits** (`customFields` endpoint) — returns a bare array of trait definitions; client-side filtering does not apply.
- **Custom Surveys** (`surveys/:id/responses`, `surveyResponses/:id`, `surveyQuestions/:id`) — uses a `{data}` envelope rather than `{results, next}`.
- **Meeting sub-resources** (`meetings/:id/participants`, `meetings/:id/transcript`) — body is returned as-is from the API.

**Trait Filtering:**

Resources supporting traits: **Accounts, Organizations, Users, Tasks, Notes, Projects, Project Templates**

Traits are excluded by default to minimise response size. To include specific traits:
1. Add `"traits"` to the `fields` parameter
2. Specify desired trait names in the `traits` parameter (comma-separated)

Example: To get account name and payment method trait:
- `fields="id,name,traits"`
- `traits="paymentMethod"`

This will return only the `paymentMethod` trait, filtering out all other traits from the response.

### Tool Structure (Tools/*.cs)

Each resource type has a dedicated tool class:
- Decorated with `[McpServerToolType]` for discovery
- Static methods decorated with `[McpServerTool]` and `[Description]`
- Pattern: `List{Resource}` and `Get{Resource}` methods
- Dependency injection: `VitallyService` injected as method parameter
- All parameters use `[Description]` attributes for MCP tool schema generation

**Example tool pattern:**
```csharp
[McpServerToolType]
public static class AccountsTools
{
    [McpServerTool, Description("List Vitally accounts...")]
    public static async Task<string> ListAccounts(
        VitallyService vitallyService,
        [Description("Maximum number...")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated fields... Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt'")] string? sortBy = null,
        [Description("Filter by account status: 'active', 'churned', 'activeOrChurned'")] string? status = null,
        [Description("Comma-separated trait names... Client-side filtering.")] string? traits = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status))
            additionalParams["status"] = status;

        return await vitallyService.GetResourcesAsync("accounts", limit, from, fields, sortBy, additionalParams, traits);
    }
}
```

**Note:** The `status` parameter is specific to `AccountsTools`, and `archived` is specific to `MeetingsTools`. The `traits` parameter is available for resources that support traits (Accounts, Organizations, Users, Tasks, Notes, Projects, Meetings, Project Templates). Other resource types have the standard parameters (limit, from, fields, sortBy).

**Raw pass-through tools:** `CustomTraitsTools`, `SurveysTools`, and the participant/transcript methods on `MeetingsTools` call `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` directly. They do not accept a `fields` parameter because Vitally returns these endpoints with a non-standard JSON envelope (`{data}` for surveys, bare arrays for `customFields`).

**Custom object instances:** `List_custom_object_instances` accepts an optional single scope
criterion — `organizationId`, `customerId`, `externalId`, or `customFieldId`+`customFieldValue`
— which routes to Vitally's `customObjects/:id/instances/search` endpoint (exactly one criterion;
paging params are ignored when scoped). `Get_custom_object_instance` reads one instance by id via
the same search endpoint (Vitally has no direct single-instance GET). The legacy free-text
`Search_custom_object_instances` tool has been removed in favour of these typed paths.

## Adding New Resource Types

To add support for a new Vitally resource:

1. Create `Tools/{ResourceName}Tools.cs` following the pattern in `AccountsTools.cs`
2. Implement `List{ResourceName}` and `Get{ResourceName}` methods
3. Use `VitallyService` with appropriate resource type string
4. **If the endpoint returns the standard `{results, next}` envelope:** add an entry to `ResourceDefaultFields` in `VitallyService.cs` with the optimised default field set
5. **If the endpoint returns a non-standard envelope** (e.g. `{data}`) or is a sub-resource path (e.g. `meetings/:id/participants`): use `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` — these bypass client-side filtering and return the body unchanged
6. **For sub-paths under an existing resource** (e.g. `admins/search`): add an explicit entry to `ResourceDefaultFields` for the full path — the lookup is exact-match, not prefix-match
7. Tools are automatically discovered via assembly scanning — no manual registration needed
8. Add a matching `Tools/{ResourceName}ToolsTests.cs` under `VitallyMcp.Tests/Tools/`

## Important Notes

- **UK English**: Use UK spelling (organisations, authorisation, etc.) in all code comments and documentation
- **Permission management**: Tools use `ReadOnly = true` flag for GET/LIST operations and `Destructive = true` flag for CREATE/UPDATE/DELETE operations. This allows MCP clients to bulk enable/disable operations by permission level.
- **Write operations**: All resources support full CRUD operations (where applicable). JSON body parameters accept complete request bodies for create/update operations.
- **Configuration**: Never hardcode credentials. Production deployments use Key Vault via managed identity; local dev uses `Vitally:DevelopmentApiKey` (env var `Vitally__DevelopmentApiKey`).
- **Error handling**: `VitallyService.SendAsync` throws `HttpRequestException` with the Vitally response body included in the message on non-2xx responses, so MCP clients see the actual failure reason in JSON-RPC errors.
- **Client-side filtering**: Field and trait selection is done client-side (Vitally API doesn't support it natively).
- **Trait filtering**: Traits are excluded by default — use the `traits` parameter to include specific trait keys (requires `"traits"` in the `fields` parameter).
- **Resource-specific defaults**: Each resource type has optimised default fields (see table above).
- **Field existence**: Only includes fields that actually exist on the resource — no null/undefined placeholders.
- **Pagination**: Use the `from` parameter (not `cursor`) — this matches the Vitally API spec.
- **JSON responses**: Tools return filtered JSON strings to reduce LLM context usage.
- **MCP SDK**: Using `ModelContextProtocol` 1.3.0 GA plus `ModelContextProtocol.AspNetCore` 1.3.0 for HTTP hosting.

## Testing

The `VitallyMcp.Tests` project contains the automated test suite (xUnit + FluentAssertions + Moq).

```powershell
# Run the full suite
dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal

# Run a single test class
dotnet test --filter "FullyQualifiedName~MeetingsToolsTests"
```

**Coverage:**
- `VitallyApiKeyProviderTests` — dev-fallback resolution (no SecretClient → returns `DevelopmentApiKey`; missing both → throws)
- `VitallyServiceTests` — JSON field/trait filtering, pagination, resource-specific defaults, plus all six service methods (`GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`, `GetRawAsync`, `PostRawAsync`, `DeleteRawAsync`) including HTTP-verb / URL / auth-header verification via Moq protected verification
- `VitallyRateLimitHandlerTests` — 429 retry behaviour, header parsing, low-remaining warnings
- `Tools/*ToolsTests` — one test class per `Tools/*Tools.cs`, covering every public `[McpServerTool]` method (list/get/create/update/delete plus sub-resources)

**When adding a new tool method:** add a matching test in the appropriate `*ToolsTests.cs` file. Use `TestHelpers.BuildVitallyService(httpClient)` — it builds a `VitallyService` with a stub `VitallyApiKeyProvider` that returns a fixed test API key (no Key Vault required).

**Manual testing considerations** (require live Vitally credentials and a real Auth0-issued token in production):
- Test pagination by using low limit values (e.g., `limit=5`) and verify `from` parameter works with `next` cursor
- Test client-side field filtering by specifying various field combinations
- Test trait filtering by combining `fields="traits"` with `traits="trait1,trait2"`
- For accounts, test the status filter with: `active`, `churned`, `activeOrChurned`
- For meetings, test the `archived` filter
- For local dev without Auth0, set `OAuth__NoAuth=true` and `Vitally__DevelopmentApiKey=<your key>`
- Verify error handling with invalid IDs / missing config

## Deployment

The deployment shape is **Azure Container Apps + Azure Key Vault + Auth0** (with Auth0 federating to Microsoft Entra for FISCAL employee sign-in), and the container image hosted in Azure Container Registry. `.github/workflows/deploy.yml` builds the image in ACR (via GitHub OIDC, no long-lived credentials) and rolls the Container App to the new revision. It is currently **manual-trigger only** (`workflow_dispatch`) until the target infrastructure is provisioned in the separate Terraform repo, at which point the `push` trigger can be enabled. It expects repo variables `ACR_NAME` / `RESOURCE_GROUP` / `CONTAINER_APP` / `IMAGE_NAME` and secrets `AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`.

| Component | Resource | Notes |
|---|---|---|
| Hosting | Azure Container Apps (consumption plan) | Scale-to-zero; HTTPS-native ingress; managed cert on `vitally.fiscaltec.com` |
| Secrets | Azure Key Vault | `vitally-shared` is the default secret name; managed identity has `Key Vault Secrets User` |
| Identity | User-assigned managed identity | `AcrPull` on the registry + `Key Vault Secrets User` on the vault |
| Image registry | Azure Container Registry (Basic SKU) | `vitally-mcp:sha-<short-sha>` tag per build; untagged purged after 7 days; ACR Task weekly purge keeps last 5 tags / 30 days |
| Logs | Log Analytics (attached to the CAE) | + Application Insights for traces |
| Auth | Auth0 tenant `fiscal-it.uk.auth0.com` | Resource Server identifier `https://vitally.fiscaltec.com`; post-login Action sets the `secret_ref` claim; tenant has **Resource Parameter Compatibility Profile** enabled to stop `resource=` forwarding to the Entra federation |
| CI/CD | GitHub Actions → OIDC federation → Azure | `deploy.yml` (manual until Terraform infra lands); no long-lived secrets in GitHub |
| IaC | Terraform (separate repo) | Infrastructure-as-code is **not** in this repo — it lives in the Terraform repo. The `deploy.yml` workflow consumes whatever that provisions. |

Infrastructure-as-code lives in the separate Terraform repo, not here — the table above is the contract for what `deploy.yml` expects. Anyone replicating can swap Container Apps for App Service, ACR for GHCR, Auth0 for Keycloak, etc., without touching the application code.
