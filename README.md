# Vitally MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) server that exposes the [Vitally](https://vitally.io) customer success platform's REST API to MCP-compatible clients such as **Claude Desktop**, **Claude Code**, **VS Code**, and **Cursor**.

Built in C# on .NET 10 and the official `ModelContextProtocol` SDK, hosted as a **remote HTTP MCP server** secured with Auth0 (OAuth 2.0 / RFC 9728). Users connect by URL — no install, no executable, no per-user secrets to distribute.

[![CI](https://github.com/fiscaltec/vitally-mcp/actions/workflows/ci.yml/badge.svg)](https://github.com/fiscaltec/vitally-mcp/actions/workflows/ci.yml)

## Features

- **Full CRUD coverage** of 92 endpoints across 17 Vitally resource types: accounts, organisations, users, conversations, messages, notes, projects, project templates, project categories, tasks, NPS responses, admins, custom objects (and instances), meetings (with participants and transcripts), custom traits, and custom surveys.
- **Permission-aware tools** — every tool is tagged `ReadOnly` / `Destructive` so MCP clients can enable or disable categories of operation in bulk.
- **EU and US data centres** — defaults to EU (`rest.vitally-eu.io`); set `Vitally:Region=US` to point at `{subdomain}.rest.vitally.io`.
- **Rate-limit-aware HTTP pipeline** — auto-retries on `429 Too Many Requests` honouring `Retry-After` and `X-RateLimit-Reset`, and logs a warning when remaining requests drop below threshold.
- **Client-side field & trait filtering** — responses are trimmed before they reach the LLM, each resource type with sensible defaults that exclude heavy fields (rich text, transcripts, full traits objects).
- **Streamable HTTP transport** (MCP 2025-06-18) in stateless mode — easy to scale horizontally, no sticky sessions required.
- **OAuth 2.0 protection** via Auth0 — `/.well-known/oauth-protected-resource` exposes the metadata document so clients discover the authorisation server automatically. The Vitally API key is fetched on demand from Azure Key Vault via the server's managed identity.

## Using the server (FISCAL users)

Point your MCP client at:

```
https://vitally.fiscaltec.com/mcp
```

The first time you connect, your client will redirect you through Auth0 to authenticate. Once authenticated, the server proxies your tool calls to Vitally using a service-account API key it fetches from Azure Key Vault.

> **Access & groups:** authentication alone grants nothing — you must be in a `sg-vitally-*` Entra group for your access tier. See **[ACCESS.md](ACCESS.md)** for connecting, the group setup, and how access is granted or revoked.

### Claude Desktop

Settings → Connectors → Add custom connector → paste the URL above. Approve the Microsoft sign-in popup.

### Claude Code

```powershell
claude mcp add --transport http vitally https://vitally.fiscaltec.com/mcp
```

Run any MCP-using command (`claude` itself, or `/mcp`) and Claude Code will open the Microsoft sign-in flow on first use.

### VS Code, Cursor, and other MCP-aware hosts

Most modern MCP clients support the streamable HTTP transport. Add a server entry that points at the URL above; the client handles the OAuth flow automatically via the protected-resource metadata document.

## Configuration

The server reads its configuration from `appsettings.json`, `appsettings.{Environment}.json`, or environment variables (using the standard ASP.NET Core double-underscore separator for nested keys, e.g. `Vitally__Region`).

| Setting | Required | Default | Description |
|---|---|---|---|
| `Vitally:Region` | No | `EU` | Data centre: `EU` (single shared host `rest.vitally-eu.io`) or `US` (per-tenant `{subdomain}.rest.vitally.io`). Case-insensitive. |
| `Vitally:Subdomain` | Only when `Region=US` | — | Vitally subdomain, e.g. `fiscaltec` from `fiscaltec.vitally.io`. Ignored on EU. |
| `Vitally:KeyVaultUri` | Yes (prod) | — | Azure Key Vault URI, e.g. `https://kv-vitally-mcp.vault.azure.net/`. The server's managed identity must have **Key Vault Secrets User** on it. |
| `Vitally:DefaultSecretRef` | No | `vitally-shared` | Key Vault secret name holding the Vitally API key. |
| `Vitally:SecretCacheDuration` | No | `00:05:00` | In-memory TTL for the resolved API key. |
| `Vitally:DevelopmentApiKey` | Yes (local) | — | Local-dev-only fallback API key, used when `KeyVaultUri` is not set. Never set this in production. |
| `OAuth:Authority` | Yes | — | OAuth/OIDC issuer URL with trailing slash, e.g. `https://fiscal-it.uk.auth0.com/`. |
| `OAuth:Audience` | Yes | — | The OAuth Resource Server / API identifier, e.g. `https://vitally.fiscaltec.com`. |
| `OAuth:Resource` | No | — | Canonical resource identifier published in `/.well-known/oauth-protected-resource`. Falls back to `Audience` when blank; set explicitly when clients need the metadata `resource` to match the server's URL/origin (per RFC 9728 + RFC 8707 validators). |
| `OAuth:PublicBaseUrl` | Recommended (prod) | — | Canonical public origin, e.g. `https://vitally.fiscaltec.com`. When set, the `/.well-known/*` metadata and the OAuth proxy callback are built from this value instead of the request `Host`, so a spoofed/forwarded `Host` can't redirect a client's `authorization_endpoint`/`token_endpoint` at an attacker. Leave empty in local dev. |
| `Authorization:Enabled` | No | `true` | Server-side RBAC enforcement. When `true`, every tool call is checked against the caller's JWT permissions (the hard backstop behind the advisory `ReadOnly`/`Destructive` flags). Set `false` only for local dev. |
| `Authorization:ReadPermission` | No | `vitally:read` | Permission required for read operations (list/get/search → HTTP GET). Must match a permission defined on the Auth0 API. |
| `Authorization:WritePermission` | No | `vitally:write` | Permission required for create/update operations (HTTP POST/PUT/PATCH). |
| `Authorization:DeletePermission` | No | `vitally:delete` | Permission required for delete operations (HTTP DELETE). Set equal to `WritePermission` to collapse to a two-tier read/write model. |
| `Authorization:CustomPermissionsClaim` | No | `https://vitally.fiscaltec.com/permissions` | Optional namespaced claim also checked for permissions (alongside the standard `permissions` and `scope` claims). Use when an Auth0 post-login Action maps Entra group membership to permissions via a custom claim instead of Auth0 role assignment. Set empty to disable. |
| `Authorization:LiveGroupCheck` | No | `false` | When `true`, permissions are resolved from the caller's **live** Entra group membership via Microsoft Graph (cached per `LiveGroupCacheSeconds`) instead of the frozen token claim — so group changes (grants/revocations) take effect within the cache window regardless of token age. The token claim is the automatic fallback if Graph is unavailable. **Requires the server's managed identity to hold Microsoft Graph `GroupMember.Read.All`.** |
| `Authorization:LiveGroupCacheSeconds` | No | `60` | TTL for the per-user live group-membership cache. Lower = faster propagation, more Graph calls. |
| `Authorization:ReaderGroupId` / `EditorGroupId` / `AdminGroupId` | When `LiveGroupCheck=true` | — | Entra security-group object ids mapped to the read / read+write / read+write+delete tiers. At least one required when live check is on. |
| `Audit:Enabled` | No | `true` | Emit a structured audit record per action (authenticated user + verb + resource + outcome). In production these flow to Application Insights / Log Analytics, giving a per-user "who did what" trail despite the shared Vitally key. |
| `Audit:IncludeReads` | No | `false` | Also audit read operations (HTTP GET). Off by default as reads are high-volume; mutations and denied attempts are always recorded. |
| `OAuth:SharedClientId` | No | — | Enables the OAuth proxy / DCR shim (see [OAuth proxy](#oauth-proxy) below). When set, every Dynamic Client Registration call returns this fixed Auth0 client_id, and the server proxies `/oauth/authorize` and `/oauth/token` to the upstream issuer. Leave empty to fall through to the upstream's native DCR. |
| `OAuth:SharedClientSecret` | No | — | Confidential-client secret for `SharedClientId`. Injected server-side on token exchange so the shared Auth0 app can stay confidential without exposing the secret to MCP clients. |
| `OAuth:AllowedClientRedirectUris` | No | `[]` | Allowlist of non-loopback `redirect_uri` values the OAuth proxy will accept. Loopback URIs (`http://localhost`, `127.0.0.1`, `[::1]`) on any port are always allowed per RFC 8252. Add cloud-hosted MCP callbacks here, e.g. `https://claude.ai/api/mcp/auth_callback`. |
| `OAuth:NoAuth` | No | `false` | **Local development only.** Skips JWT validation entirely. Logs a warning at startup. |

See [`VitallyMcp/appsettings.Example.json`](VitallyMcp/appsettings.Example.json) for the full layout.

## Running locally

Prerequisites: .NET 10 SDK.

```powershell
# Restore + build + run the test suite
dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal

# Start the server in dev mode (no Auth0, no Key Vault — uses DevelopmentApiKey from env)
$env:OAuth__NoAuth = "true"
$env:Vitally__Region = "EU"
$env:Vitally__DevelopmentApiKey = "sk_live_your_key"
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project VitallyMcp/VitallyMcp.csproj
```

Smoke test:

```powershell
# OAuth protected-resource metadata
Invoke-RestMethod http://localhost:5099/.well-known/oauth-protected-resource

# MCP initialise (returns capabilities + server info)
$body = @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-06-18'; capabilities=@{}; clientInfo=@{ name='smoke'; version='0.0.1' } } } | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body
```

Then add the dev server to Claude Code:

```powershell
claude mcp add --transport http vitally-dev http://localhost:5099/mcp
```

## Self-host (replicators)

Deploying your own instance for a different org or against a different Vitally tenant requires three things — none of which are in this repo, all of which are config:

1. **An OIDC identity provider** that issues RS256-signed JWTs for your users. Auth0 ID is what FISCAL uses; any compliant provider works (Auth0, Keycloak, Okta, etc.). Register an Application with identifier URI matching your `OAuth:Audience` value, plus a delegated scope (e.g. `Tools.Access`) and public-client redirect URI `http://localhost` for MCP-client OAuth flows.
2. **An Azure Key Vault** (or compatible secret store; see the swap notes in [CLAUDE.md](CLAUDE.md)) containing your Vitally API key as a secret. Default secret name is `vitally-shared`; change via `Vitally:DefaultSecretRef`.
3. **A container host** that can run the published Docker image. Anywhere ASP.NET Core 10 runs (Azure Container Apps, AWS App Runner, GCP Cloud Run, plain Kubernetes) — Container Apps is what FISCAL uses.

FISCAL's deployment uses Azure Container Apps + Azure Key Vault + Auth0 (which federates to Microsoft Entra for sign-in) — see the [Deployment](CLAUDE.md#deployment) section in `CLAUDE.md` for the shape. Anyone replicating can swap Container Apps for App Service, ACR for GHCR, Auth0 for Keycloak, etc., without touching the application code. Bicep / `azd` templates aren't shipped in this repo — the surface is small enough that the README description is the contract.

## Architecture

```
VitallyMcp/
├── Program.cs                       # ASP.NET Core host, JwtBearer auth, MapMcp
├── OAuthOptions.cs                  # Authority + Audience + Resource + NoAuth dev flag
├── VitallyServerOptions.cs          # Region, KeyVaultUri, secret config
├── VitallyApiKeyProvider.cs         # Fetches the API key from Key Vault (cached)
├── VitallyService.cs                # HTTP client + client-side JSON filtering
├── VitallyRateLimitHandler.cs       # 429 retry + rate-limit warnings
└── Tools/                           # One file per Vitally resource type
    ├── AccountsTools.cs
    ├── OrganizationsTools.cs
    ├── UsersTools.cs
    ├── ConversationsTools.cs
    ├── MessagesTools.cs
    ├── NotesTools.cs
    ├── ProjectsTools.cs
    ├── ProjectTemplatesTools.cs
    ├── TasksTools.cs
    ├── NpsResponsesTools.cs
    ├── AdminsTools.cs
    ├── CustomObjectsTools.cs
    ├── MeetingsTools.cs
    ├── CustomTraitsTools.cs
    └── SurveysTools.cs
```

The MCP server runs on the [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) package using the streamable HTTP transport in stateless mode. `MapMcp("/mcp")` is gated by `RequireAuthorization()` — JWTs are validated against the Auth0 tenant configured in `OAuth:Authority` / `OAuth:Audience`. On each tool call, `VitallyApiKeyProvider` fetches the `vitally-shared` secret from Key Vault (cached in-memory for 5 min, using the server's user-assigned managed identity), and `VitallyService` uses it to call Vitally on behalf of all authenticated users.

### OAuth proxy

When `OAuth:SharedClientId` is set the server runs an OAuth 2.0 proxy in front of the upstream Auth0 tenant. It serves:

| Endpoint | Purpose |
|---|---|
| `GET /.well-known/oauth-protected-resource` | RFC 9728 protected-resource metadata — clients use it to discover the authorisation server. |
| `GET /.well-known/oauth-authorization-server` | RFC 8414 authorisation-server metadata — points `authorization_endpoint` and `token_endpoint` at the proxy and `registration_endpoint` at our DCR shim. |
| `GET /oauth/authorize` | Captures the client's `redirect_uri`, swaps it for our fixed `/oauth/callback`, and 302s the user upstream to Auth0. Validates the client `redirect_uri` against the loopback + allowlist rules before stashing. |
| `GET /oauth/callback` | Receives the Auth0 redirect, looks up the original client `redirect_uri` from `state`, and 302s the user back to it with the code. |
| `POST /oauth/token` | Forwards the code-exchange to Auth0 and injects `SharedClientSecret` so the shared app stays confidential without exposing the secret to MCP clients. |
| `POST /oauth/register` | RFC 7591 Dynamic Client Registration shim — always returns `SharedClientId`, regardless of what the caller requests, so every MCP client converges on a single first-party Auth0 app. Echoes back only `redirect_uris` that the allowlist accepts. |

This setup exists because MCP clients implement RFC 7591 (DCR) and RFC 8252 (loopback redirect with ephemeral ports), but Auth0 third-party DCR clients trigger a per-session API consent screen and don't natively accept arbitrary loopback ports. The proxy collapses everything onto one pre-registered "first-party" Auth0 app, skipping the consent and accepting any loopback port (Claude Code, VS Code, Cursor, MCP Inspector all rotate ports between sessions). To support hosted MCP clients (e.g. Claude.ai), add their callback URL to `OAuth:AllowedClientRedirectUris`.

The `VitallyService` exposes two call patterns:
1. **Standard envelope** (`GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`) — for endpoints returning `{results, next}`. Applies client-side field and trait filtering with resource-specific defaults.
2. **Raw passthrough** (`GetRawAsync`, `PostRawAsync`, `DeleteRawAsync`) — for endpoints whose response shape differs from the standard envelope (surveys' `{data}`, custom-fields' bare array) or for sub-resource sub-paths (meeting participants, meeting transcripts).

All HTTP traffic flows through `VitallyRateLimitHandler`, a `DelegatingHandler` registered via `AddHttpMessageHandler<>()` in `Program.cs`.

## Tool catalogue

The server publishes 92 MCP tools, one per Vitally REST endpoint. Each tool's `[McpServerTool]` attribute sets `ReadOnly = true` for list/get operations and `Destructive = true` for create/update/delete, so MCP clients can permission them in bulk.

| Resource | List / search | Get | Create | Update | Delete | Sub-resources |
|---|:-:|:-:|:-:|:-:|:-:|---|
| Accounts | ✓ | ✓ | ✓ | ✓ | ✓ | health-score breakdown |
| Organizations | ✓ | ✓ | ✓ | ✓ | ✓ | — |
| Users | ✓ (+search) | ✓ | ✓ | ✓ | ✓ | by account, by organisation |
| Conversations | ✓ | ✓ | ✓ | ✓ | ✓ | by account, by organisation |
| Messages | ✓ | ✓ | ✓ | — | ✓ | by conversation |
| Notes | ✓ | ✓ | ✓ | ✓ | ✓ | by account, by organisation, note categories |
| Projects | ✓ | ✓ | ✓ (from template) | ✓ | ✓ | by account, by organisation |
| Project templates | ✓ | ✓ | — | — | — | project categories |
| Tasks | ✓ | ✓ | ✓ | ✓ | ✓ | by account, by organisation, task categories |
| NPS responses | ✓ | ✓ | ✓ | ✓ | ✓ | by account, by organisation |
| Admins | search by email | — | — | — | — | — |
| Custom objects | ✓ | ✓ | ✓ | ✓ | — | instances (list, search, CRUD) |
| Meetings | ✓ | ✓ | ✓ | ✓ | ✓ | by account, by organisation, participants, transcripts |
| Custom traits | schema discovery | — | — | — | — | — |
| Custom surveys | responses (list, get) | survey question | — | — | — | — |

Full per-tool descriptions are auto-generated from the `[McpServerTool]` attributes — call `tools/list` against the server to see them all.

## Security

- All MCP requests require a valid JWT signed by the configured Auth0 tenant. Tokens are validated server-side against the issuer + audience and the signature.
- **Server-side RBAC** (`Authorization:*`) enforces a `vitally:read` / `vitally:write` / `vitally:delete` permission on every tool call, mapped from the HTTP verb at a single choke point (`VitallyService.SendAsync`). This is the hard backstop: the `ReadOnly`/`Destructive` tool attributes are advisory hints for MCP clients, but RBAC physically prevents a caller (or a misbehaving agent) from mutating data without the permission. Permissions are sourced from Auth0 (Enable RBAC + Add Permissions in the Access Token) and assigned via roles — ideally driven by Entra group membership.
- **Per-user audit trail** (`Audit:*`) — every action is logged at the choke point with the authenticated user's stable Entra subject id (`sub` — an opaque object id, resolvable to a person in Entra but not itself PII), HTTP verb, target resource path and outcome (denied attempts included). Because all users share one Vitally key, Vitally's own log can't attribute actions to individuals; this server-side record can, and is queryable in Application Insights / Log Analytics. Neither email nor request bodies are logged, keeping personal data out of telemetry while staying fully attributable.
- The OAuth proxy's `/oauth/token` only services the `authorization_code` and `refresh_token` grants — it rejects any other grant before injecting the confidential client secret, so the secret can't be leveraged to mint tokens without a user sign-in.
- Set `OAuth:PublicBaseUrl` in production so the OAuth metadata documents emit a fixed canonical origin rather than reflecting the request `Host`.
- Vitally API keys are **not** distributed to clients or stored in tokens — they live in Key Vault, accessed by the server's managed identity.
- Tokens are short-lived (8h access, with refresh rotation). The server keeps no session state; restart is transparent to clients.
- HTTPS is terminated at the platform ingress (Container Apps managed cert) — the server itself doesn't ship TLS.
- The OAuth proxy validates every client `redirect_uri` against `OAuth:AllowedClientRedirectUris` (plus the implicit RFC 8252 loopback rule). Without this check, an attacker could exfiltrate authorisation codes via the proxy's `/oauth/callback` reflector; with it, the proxy refuses anything that isn't a loopback URI or an explicitly-allowlisted hosted callback.

## Licence

Proprietary — © FISCAL Technologies Ltd. All rights reserved.

## Support

- Internal: Infrastructure team at FISCAL Technologies.
- Issues: [GitHub Issues](https://github.com/fiscaltec/vitally-mcp/issues).
