<!--
  Copyright (c) 2024 John Jung & Dan Searle (original)
  Copyright (c) 2026 Wiseair S.r.l. (fork)
-->

# Vitally MCP Server (Wiseair fork)

An MCP (Model Context Protocol) server for the Vitally REST API.

This is the Wiseair fork of [`fiscaltec/vitally-mcp`](https://github.com/fiscaltec/vitally-mcp). It exists because the upstream tools strip account traits (ARR, MRR, renewal date, sentiment, custom traits) out of every list/search result, leaving the LLM unable to answer basic questions like "rank our customers by ARR" without scraping notes for context. This fork fixes that and fills in the missing workspace-level list endpoints.

**Image**: `ghcr.io/wiseair-srl/vitally-mcp:latest` (also tagged with semver: `v2.0.0`, `v2`, `v2.0`).

## Differences from upstream

| Upstream | Wiseair fork |
|---|---|
| Account list/search returns `{id, name, externalId, uri}` only — traits hidden | Full account payload (traits, MRR, NPS, health, CSM, renewal date) on every account-returning tool |
| No direct `get_account` by id/externalId | New `get_account` tool |
| No workspace-level list endpoints — only per-account | New `list_accounts`, `list_tasks`, `list_conversations`, `list_notes`, `list_projects`, `list_organizations` |
| No `update_account` — cannot set traits | New `update_account` (PUT /resources/accounts/:id) |
| No direct `get_user` by id | New `get_user` |
| No `get_project` | New `get_project` |
| Hand-maintained `search_tools` registry that drifted from the real tool list | `search_tools` derives from the same array that powers `tools/list`, so it cannot drift |
| API errors swallowed as `API call failed: 4xx` with no body | Error body surfaced verbatim in the MCP error |
| No rate-limit awareness | Logs to stderr when `X-RateLimit-Remaining` falls below 50 |
| No cursor pagination — `refresh_accounts` only fetched first page silently | All list tools accept a `from` cursor and return `{results, next}`; pagination is caller-driven |

Tool names and signatures from upstream are preserved; the only response-shape change is **additive** (search/find/refresh-accounts now embed the full account object — old fields still present). See `CHANGELOG.md`.

## Tools

### Tool discovery
- `search_tools` — keyword search across the live tool list (auto-derived).

### Accounts
- `get_account` — full account payload by Vitally ID **or** externalId.
- `list_accounts` — paginated list with `limit`, `from`, `status` (`active` | `churned` | `activeOrChurned`).
- `update_account` — `PUT /resources/accounts/:id`. Set traits (e.g. ARR, tier, sentiment) or rename. Trait values can be set to `null` to clear them.
- `search_accounts` — cached single-page name/externalId search; full payload by default. For full enumeration use `list_accounts`.
- `find_account_by_name` — same as above for name-only lookups.
- `refresh_accounts` — reloads the in-memory cache.
- `get_account_health` — health score breakdown.

### Workspace-level lists (paginated, `{results, next}`)
- `list_tasks` — `limit`, `from`, `archived`. The Vitally API does **not** support server-side filtering by status / assignee / due-date here; filter client-side after retrieval.
- `list_conversations` — `limit`, `from`.
- `list_notes` — `limit`, `from`, `archived`. Optional `accountId` switches to the per-account endpoint.
- `list_projects` — `limit`, `from`, `archived`.
- `list_organizations` — `limit`, `from`.

### Per-account scoped tools
- `get_account_conversations`, `get_account_tasks`, `get_account_notes`, `create_account_note`.

### Notes & projects
- `get_note_by_id`, `get_project`.

### Users
- `search_users` — by email / externalId / emailSubdomain.
- `get_user` — direct lookup by Vitally ID or externalId.

Every tool that returns an account uses the same `serializeAccount` helper, so the shape is consistent across the codebase.

### `includeTraits` opt-out
`search_accounts`, `find_account_by_name`, `refresh_accounts`, and `list_accounts` accept `includeTraits: false` to fall back to the upstream slim shape `{id, name, externalId, uri}` — useful when listing thousands of rows.

## Running via Docker (recommended)

Add this to your Claude Desktop config (`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS):

```json
{
  "mcpServers": {
    "vitally": {
      "command": "docker",
      "args": [
        "run", "--rm", "-i",
        "-e", "VITALLY_API_SUBDOMAIN",
        "-e", "VITALLY_API_KEY",
        "-e", "VITALLY_DATA_CENTER",
        "ghcr.io/wiseair-srl/vitally-mcp:latest"
      ],
      "env": {
        "VITALLY_API_SUBDOMAIN": "your-subdomain",
        "VITALLY_API_KEY": "your-api-key",
        "VITALLY_DATA_CENTER": "EU"
      }
    }
  }
}
```

### Migrating from `fiscaltec/vitally-mcp`

Replace `ghcr.io/fiscaltec/vitally-mcp` with `ghcr.io/wiseair-srl/vitally-mcp:latest` in your config and restart Claude Desktop. Tool names and required parameters are unchanged. The only behaviour change you'll notice is that account-returning tools now include the full payload (traits, MRR, etc) — pass `includeTraits: false` to opt back into the slim shape.

## Running locally

```bash
pnpm install
cp .env.example .env  # then edit it
pnpm run build
pnpm start
```

Without `VITALLY_API_KEY` (or with the placeholder value), the server runs in **demo mode** with mock data — useful for end-to-end testing in MCP Inspector without a Vitally account.

### Smoke test

```bash
pnpm run build && pnpm test
```

Runs against demo mode by default. Set `VITALLY_API_KEY` to point at real Vitally creds (the demo-specific Sace asserts are skipped automatically).

## Configuration

Environment variables (all read at startup, never logged in plain text):

| Var | Default | Notes |
|---|---|---|
| `VITALLY_API_KEY` | — | Required for non-demo mode. Get from Vitally → Settings → Integrations → Vitally REST API. |
| `VITALLY_API_SUBDOMAIN` | `nylas` | Used for the US data center base URL: `https://{subdomain}.rest.vitally.io`. |
| `VITALLY_DATA_CENTER` | `US` | `US` or `EU`. EU uses a single shared host: `https://rest.vitally-eu.io`. |

## Registry choice

We publish to **GitHub Container Registry (GHCR)** at `ghcr.io/wiseair-srl/vitally-mcp`:

- Same registry the upstream uses, so consumers swapping the namespace get a one-line config change.
- Auth is the GitHub token already in CI; no extra secrets needed.
- Free for public images, attestations work out of the box.

We do not mirror to Docker Hub. If that becomes an actual constraint we can revisit.

## Attribution

Original code by [John Jung](https://github.com/johnjjung/vitally-mcp), containerised by [Dan Searle](https://github.com/fiscaltec). MIT-licensed (preserved). The Wiseair fork adds the changes described above.

## Notes

- Don't add `console.log` anywhere — the MCP stdio transport uses stdout for JSON-RPC and any stray write corrupts the protocol stream. Use the `log()` helper which writes to stderr.
- The Vitally REST API is rate-limited. The server logs to stderr when fewer than 50 calls remain on the current window; if your workflow runs into hard limits, paginate explicitly via `from` cursors instead of repeatedly calling `refresh_accounts`.
