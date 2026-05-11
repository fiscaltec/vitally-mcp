# Changelog

## v2.2.0 — 2026-05-11

### Fixed
- **`create_account_note`** was 100% broken: it called `POST /resources/accounts/:id/notes` (route does not exist on Vitally → 404) with body `{ content }` (wrong schema). Now calls `POST /resources/notes` with `{ accountId, note, noteDate }`. The tool input parameter is renamed `content` → `note` to match Vitally's field name; `noteDate` is a new optional parameter that defaults to `now()`.

### Changed
- **MCP error contract.** Tool execution failures now return a `CallToolResult` with `isError: true` and a human-readable `text` payload containing the exception name and message (and, for HTTP failures from `callVitallyAPI`, the Vitally status code and response body). Previously the handler rethrew, which the MCP SDK serialized as a JSON-RPC protocol error and the client (Claude.ai) rendered as a generic "Tool execution failed" with no diagnostic content. The happy path is unchanged.
- Server `name` advertised over MCP bumped to `vitally-api` v2.2.0.

### Migration
- Callers of `create_account_note` must rename `content` → `note`. Optional: pass `noteDate` (ISO 8601) to control the timestamp; otherwise it defaults to `now()`.
- Programmatic clients that previously caught JSON-RPC errors from `tools/call` should now also inspect `result.isError` on successful RPC responses.

## v2.1.0 — 2026-05-05

### Added
- **`aggregate_accounts`** — group/aggregate the cached account list. Supports `count`, `sum`, `avg`, `min`, `max` over a trait or top-level field, with optional `groupBy`, `filterTraits`, `status`, and `sortByMetric`.
- **`traits` projection** on `search_accounts`, `find_account_by_name`, `list_accounts` — pass `traits: ["vitally.custom.arr", ...]` to project only those keys per row instead of the full traits dict. Implies `includeTraits=true`. Conflicts with `includeTraits=false` are surfaced as a `warning` field; `traits` wins.
- **`sortBy` / `sortOrder`** on `list_accounts` — sort by top-level field (`mrr`, `usersCount`, `nextRenewalDate`, `createdAt`, `updatedAt`, `healthScore`, `npsScore`, `name`) or trait key (e.g. `vitally.custom.arr`). Nulls always sort last regardless of order. Uses the cache; refreshes if empty.
- **`filterTraits`** on `list_accounts` — exact-match filter on trait key/value pairs. Combines with `sortBy` and `limit`. Cache-mode.
- **`includeAccount`** on per-account list endpoints (`get_account_tasks`, `get_account_notes`, `get_account_conversations`, `list_tasks`, `list_notes`, `list_conversations`). Defaults to `false` — the embedded `account` object is stripped, `accountId` is preserved. Pass `includeAccount: true` to restore the previous shape.
- **`descriptionFormat`** on `get_account_tasks`, `list_tasks`, `get_account_notes`, `list_notes`, `get_note_by_id`. Defaults to `'plain'`: HTML tags stripped, `<br>`/`<p>` boundaries become newlines, `<img>` becomes `[image]`, entities decoded. Pass `'html'` to keep raw HTML.
- **Workspace-level `_warnings`** on `get_account`, `list_accounts`, `aggregate_accounts`. On the first call per server lifetime, samples up to 20 accounts; if `healthScore` (or `npsScore`) is null on every sampled row, a one-time warning is emitted explaining that the workspace likely has no health/NPS configured. Each warning is emitted at most once per session.
- **Guarded traits on `update_account`** — writing one of `vitally.custom.arr`, `vitally.custom.mrr`, `vitally.custom.status`, `vitally.custom.churnDate`, `vitally.custom.nextRenewal`, `vitally.custom.currentSubscriptionStartDate`, `vitally.custom.testAccount` now requires `force: true`. These flow in from system-of-record sources; the LLM should not silently overwrite them.

### Changed
- `get_account_tasks`: the `status` parameter (`open` | `completed` | `archived`) is now applied client-side and respected. The MCP scans up to 5 upstream pages to fill `limit`; `pagesScanned` and `truncated` are returned alongside `next`/`results`. `'open'` = `!completedAt && !archivedAt`, `'completed'` = `completedAt` set, `'archived'` = `archivedAt` set.
- All tool descriptions audited and tightened for LLM-facing clarity (when to use vs sibling tools, silent gotchas, default values).
- Server `name` advertised over MCP bumped to `vitally-api` v2.1.0.

### Deprecated
- `find_account_by_name` — now forwards to `search_accounts` and tags responses with `_deprecation`. Will be removed in a future release. Use `search_accounts` for both name-only lookups and combined name + externalId queries.

### Fixed
- `status` filter on `get_account_tasks` previously appeared to work but the upstream Vitally API silently ignored it. Now enforced client-side with explicit semantics.

### Migration

No breaking changes for callers using documented parameters. Two behavioural shifts to be aware of:

1. `get_account_tasks`, `list_tasks`, `get_account_notes`, `list_notes`, `get_account_conversations`, `list_conversations` no longer embed the full `account` object on each row by default. If you depended on this, pass `includeAccount: true`.
2. Task `description` and note `content` come back as plain text by default. If you depended on the raw HTML, pass `descriptionFormat: 'html'`.

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
