<!--
  Copyright (c) 2024 John Jung & Dan Searle (original)
  Copyright (c) 2026 Wiseair S.r.l. (fork)
-->

# Vitally MCP Server (Wiseair fork)

MCP (Model Context Protocol) server for the Vitally REST API. Fork of [`fiscaltec/vitally-mcp`](https://github.com/fiscaltec/vitally-mcp) — adds full account payloads on every account-returning tool, workspace-level list endpoints, `update_account`, cursor pagination, and `aggregate_accounts`.

**Image:** `ghcr.io/wiseair-srl/vitally-mcp:latest` (also tagged `v2`, `v2.1`, `v2.1.0`).

## Quick start (Docker)

Add to `~/Library/Application Support/Claude/claude_desktop_config.json` and restart Claude Desktop:

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

**Migrating from `fiscaltec/vitally-mcp`?** Swap the image string. Tool names and required parameters are unchanged. Account-returning tools now embed the full payload by default — pass `includeTraits: false` to opt back into the slim shape.

## Configuration

| Var | Default | Notes |
|---|---|---|
| `VITALLY_API_KEY` | — | Required outside demo mode. Vitally → Settings → Integrations → REST API. |
| `VITALLY_API_SUBDOMAIN` | `nylas` | US base URL: `https://{subdomain}.rest.vitally.io`. |
| `VITALLY_DATA_CENTER` | `US` | `US` or `EU`. EU uses `https://rest.vitally-eu.io`. |

Without `VITALLY_API_KEY` (or with a placeholder value) the server runs in **demo mode** with mock data — useful for end-to-end testing in MCP Inspector without a Vitally account.

## Tools

**Discovery** — `search_tools` (keyword search across the live tool list).

**Accounts** — `get_account`, `list_accounts`, `search_accounts`, `find_account_by_name` (deprecated → `search_accounts`), `update_account`, `refresh_accounts`, `get_account_health`, `aggregate_accounts`.

**Per-account** — `get_account_conversations`, `get_account_tasks`, `get_account_notes`, `create_account_note`.

**Workspace-level lists** (paginated `{results, next}`) — `list_tasks`, `list_conversations`, `list_notes`, `list_projects`, `list_organizations`.

**Notes & projects** — `get_note_by_id`, `get_project`.

**Users** — `search_users`, `get_user`.

### Payload shaping

Common params across account-returning tools:

- `includeTraits: false` → slim `{id, name, externalId, uri}` shape.
- `traits: ["vitally.custom.arr", ...]` → project only those trait keys per row.
- `includeAccount: false` (default on per-row list tools) → strip embedded account, keep `accountId`.
- `descriptionFormat: 'plain' | 'html'` (default `'plain'`) → strip HTML on task/note descriptions.
- `from` cursor on every list tool; pagination is caller-driven.

## What changed from upstream

- Account list/search/find embed the **full account payload** (traits, MRR, NPS, health, CSM, renewal date) — old fields preserved.
- New tools: `get_account`, `update_account`, `get_user`, `get_project`, `aggregate_accounts`, plus the workspace-level lists above.
- `update_account` guards trait writes to system-of-record fields (ARR, MRR, status, churn dates, test flag) behind `force: true`.
- `list_accounts` supports server-side `sortBy` / `sortOrder` / `filterTraits` via the in-memory cache for fast top-N queries.
- `search_tools` is derived from the same array that powers `tools/list` — cannot drift.
- API errors surface the upstream response body verbatim. Rate-limit warnings hit stderr when fewer than 50 calls remain.

See [`CHANGELOG.md`](CHANGELOG.md) for the full release history.

## Local development

```bash
pnpm install
cp .env.example .env  # then edit
pnpm run build
pnpm start
```

Smoke test (runs against demo mode by default):

```bash
pnpm run build && pnpm test
```

> **Note:** the MCP stdio transport uses stdout for JSON-RPC. Never add `console.log` — use the `log()` helper, which writes to stderr.

## Attribution

Original code by [John Jung](https://github.com/johnjjung/vitally-mcp), containerised by [Dan Searle](https://github.com/fiscaltec). MIT-licensed (preserved).
