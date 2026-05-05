# Changelog

## v2.0.0 — 2026-05-05 (Wiseair fork)

First Wiseair release, forked from `fiscaltec/vitally-mcp` v1.0.1. Major version bump signals divergence from upstream; tool names and required parameters are unchanged but several response shapes are richer.

### Added
- **`get_account`** — direct lookup by Vitally ID or externalId. Returns the full account object (traits, MRR, NPS, health, CSM, renewal date, segments, …).
- **Workspace-level list tools**: `list_accounts`, `list_tasks`, `list_conversations`, `list_notes`, `list_projects`, `list_organizations`. All paginated via the `from`/`next` cursor protocol.
- **`update_account`** — `PUT /resources/accounts/:id` for setting traits or renaming. Trait values can be `null` to clear.
- **`get_user`** — direct user lookup by id/externalId.
- **`get_project`** — direct project lookup.
- **`includeTraits`** option on `search_accounts`, `find_account_by_name`, `refresh_accounts`, `list_accounts` — set to `false` to fall back to the upstream slim shape.
- `.env.example` and `.dockerignore`.

### Changed (additive — old fields still present)
- `search_accounts`, `find_account_by_name`, `refresh_accounts` now embed the full account payload (traits, MRR, etc) by default. Use `includeTraits: false` for the old slim shape.
- All tools that return an account use a single `serializeAccount` helper. Same shape everywhere.
- All workspace-level GET tools return the raw `{results, next}` shape. The caller drives pagination — the server **does not** silently fetch all pages.
- `search_tools` now derives its result list from the same `TOOL_DEFINITIONS` array that powers `tools/list`. The hand-maintained registry that drifted from the real tool list is gone.
- Errors from the Vitally API now include the response status, status text, and body in the MCP error message.
- The server logs to stderr when `X-RateLimit-Remaining` falls below 50.
- `VITALLY_DATA_CENTER=EU` confirmed against the docs to use the shared host `https://rest.vitally-eu.io` (no per-tenant subdomain).
- Server `name` advertised over MCP is now `vitally-api` v2.0.0.
- Image is published from CI to `ghcr.io/wiseair-srl/vitally-mcp` with full semver tags (`latest`, `v2`, `v2.0`, `v2.0.0`) and build provenance attestation.

### Fixed
- `accountByIdMatch` regex in mock mode (the upstream check `endpoint.startsWith('/resources/accounts/') && !endpoint.includes('/')` was always false and the mock fall-through never returned a single account).
- Removed the dead `transport: { type: "http-stream" ... }` block from the `Server` constructor — the actual transport is `StdioServerTransport`, the inline config never did anything.
- `console.error` env-load messages in the upstream were already on stderr (harmless), but every diagnostic now goes through a single `log()` helper to make accidental `console.log` introductions impossible to mistake for an existing pattern.

### Removed
- The hand-maintained `AVAILABLE_TOOLS` array (replaced by `TOOL_DEFINITIONS`, the same array that drives `tools/list`).

### Migration

Replace `ghcr.io/fiscaltec/vitally-mcp` with `ghcr.io/wiseair-srl/vitally-mcp:latest` in your MCP client config. No prompt or schema changes required. Pass `includeTraits: false` to any account-list tool if the larger payloads bloat your context.
