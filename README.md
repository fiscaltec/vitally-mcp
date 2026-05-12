# Vitally MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io) server that exposes the [Vitally](https://vitally.io) customer success platform's REST API to MCP-compatible clients such as **Claude Desktop** and **Claude Code**.

Built in C# on .NET 10 and the official `ModelContextProtocol` SDK, distributed as both a Windows MCPB bundle (Claude Desktop) and a standalone single-file executable (Claude Code or any direct-launch MCP host).

[![Build status](https://github.com/fiscaltec/vitally-mcp/actions/workflows/release.yml/badge.svg)](https://github.com/fiscaltec/vitally-mcp/actions/workflows/release.yml)

## Features

- **Full CRUD coverage** of 93 endpoints across 17 Vitally resource types: accounts, organisations, users, conversations, messages, notes, projects, project templates, project categories, tasks, NPS responses, admins, custom objects (and instances), meetings (with participants and transcripts), custom traits, and custom surveys.
- **Permission-aware tools** — every tool is tagged `ReadOnly` / `Destructive` so MCP clients can enable or disable categories of operation in bulk.
- **EU and US data centres** — defaults to EU (`rest.vitally-eu.io`); set `VITALLY_REGION=US` to point at `{subdomain}.rest.vitally.io`.
- **Rate-limit-aware HTTP pipeline** — auto-retries on `429 Too Many Requests` honouring `Retry-After` and `X-RateLimit-Reset`, and logs a warning when remaining requests drop below threshold.
- **Client-side field & trait filtering** — responses are trimmed before they reach the LLM, each resource type with sensible defaults that exclude heavy fields (rich text, transcripts, full traits objects).
- **In-process update check** — the `Check_for_updates` MCP tool reports whether a newer GitHub Release is available and returns architecture-matched download URLs.

## Installation

Pre-built artefacts are published to [GitHub Releases](https://github.com/fiscaltec/vitally-mcp/releases). Every release contains four binaries plus a `SHA256SUMS.txt`:

| File | Audience |
|---|---|
| `VitallyMcp-{version}-win-x64.mcpb` | Claude Desktop on Intel/AMD64 |
| `VitallyMcp-{version}-win-arm64.mcpb` | Claude Desktop on ARM64 |
| `VitallyMcp-{version}-win-x64.exe` | Claude Code (or any direct-launch host) on Intel/AMD64 |
| `VitallyMcp-{version}-win-arm64.exe` | Claude Code on ARM64 |

### Claude Desktop

1. Download the `.mcpb` file for your architecture from the latest release.
2. Double-click — Claude Desktop installs the server and prompts for the configuration values declared in `manifest.json`.
3. Provide `VITALLY_API_KEY`. Leave `VITALLY_REGION` blank to use the default (EU), or set it to `US` and supply `VITALLY_SUBDOMAIN` for a US tenant.
4. Restart Claude Desktop.

### Claude Code

Claude Code expects a path to an executable. Download the `.exe` for your architecture (e.g. to `C:\Tools\VitallyMcp\VitallyMcp.exe`) and add the server to your MCP config (`~/.claude.json` or project-local `.mcp.json`):

```json
{
  "mcpServers": {
    "vitally": {
      "command": "C:\\Tools\\VitallyMcp\\VitallyMcp.exe",
      "env": {
        "VITALLY_API_KEY": "sk_live_your_key",
        "VITALLY_REGION": "EU"
      }
    }
  }
}
```

For a US tenant, swap to `"VITALLY_REGION": "US"` and add `"VITALLY_SUBDOMAIN": "your-subdomain"`.

## Configuration

| Variable | Required | Default | Description |
|---|---|---|---|
| `VITALLY_API_KEY` | Yes | — | Vitally API key, format `sk_live_*`. Generate one under **Settings → Integrations → API Keys** in Vitally. |
| `VITALLY_REGION` | No | `EU` | Data centre: `EU` (single shared host `rest.vitally-eu.io`) or `US` (per-tenant `{subdomain}.rest.vitally.io`). Case-insensitive. |
| `VITALLY_SUBDOMAIN` | Only when `VITALLY_REGION=US` | — | Your Vitally subdomain, e.g. `fiscaltec` from `fiscaltec.vitally.io`. Ignored on EU because the EU API has no per-tenant subdomain. |

## Updating

Inside the host, ask Claude to call `Check_for_updates`. The tool returns:
- the running version,
- the latest GitHub Release version,
- whether an update is available, and
- pre-resolved download URLs matching your architecture for both `.mcpb` (Claude Desktop) and `.exe` (Claude Code).

To apply an update:
- **Claude Desktop**: download the new `.mcpb` and double-click to reinstall.
- **Claude Code**: download the new `.exe` and replace the file referenced by `command` in your MCP config.

There's no self-replacing binary by design: the Windows file lock on a running .exe makes that fragile, and the manual swap takes only a few seconds.

## Tool catalogue

The server publishes ~95 MCP tools, one per Vitally REST endpoint. Each tool's `[McpServerTool]` attribute sets `ReadOnly = true` for list/get operations and `Destructive = true` for create/update/delete, so MCP clients can permission them in bulk.

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
| Updates | check for updates | — | — | — | — | — |

Full per-tool descriptions are in [`Output/mcpb/manifest.json`](Output/mcpb/manifest.json) and the developer guide in [`CLAUDE.md`](CLAUDE.md).

## Building from source

Prerequisites: .NET 10 SDK, Node.js 20+, PowerShell 7+, and the MCPB CLI (`npm install -g @anthropic-ai/mcpb`).

```powershell
# Restore + build + run the test suite (200+ tests)
dotnet test VitallyMcp.sln -c Release --nologo --verbosity minimal

# Standalone exe (Claude Code), auto-detects architecture
.\Scripts\build-standalone.ps1

# MCPB bundle (Claude Desktop), auto-detects architecture
.\Scripts\build-mcpb.ps1

# Both, with a version bump
.\Scripts\build-all.ps1                # Revision bump
.\Scripts\build-all.ps1 -BumpType Minor
.\Scripts\build-all.ps1 -BumpType Major
```

See [`CLAUDE.md`](CLAUDE.md) for the deeper development guide, including the `VitallyService` filtering model, the rate-limit handler, the raw-passthrough pattern, and per-resource default field sets.

## Releases

Releases are produced automatically by [`.github/workflows/release.yml`](.github/workflows/release.yml) when a `vX.Y.Z` tag is pushed. The workflow pins the `.csproj` and manifest versions to the tag, runs the test suite, builds `win-x64` and `win-arm64` artefacts for both Claude Desktop and Claude Code, computes SHA-256 checksums, and creates a GitHub Release with auto-generated notes.

## Architecture

```
VitallyMcp/
├── Program.cs                       # Host + DI setup, MCP stdio server
├── VitallyConfig.cs                 # Env-var loading and region selection
├── VitallyService.cs                # HTTP client + client-side JSON filtering
├── VitallyRateLimitHandler.cs       # 429 retry + rate-limit warnings
├── UpdateCheckService.cs            # GitHub Releases probe for Check_for_updates
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
    ├── SurveysTools.cs
    └── UpdatesTools.cs
```

The `VitallyService` exposes two call patterns:
1. **Standard envelope** (`GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`) — for endpoints returning `{results, next}`. Applies client-side field and trait filtering with resource-specific defaults.
2. **Raw passthrough** (`GetRawAsync`, `PostRawAsync`, `DeleteRawAsync`) — for endpoints whose response shape differs from the standard envelope (surveys' `{data}`, custom-fields' bare array) or for sub-resource sub-paths (meeting participants, meeting transcripts).

All HTTP traffic flows through `VitallyRateLimitHandler`, a `DelegatingHandler` registered via `AddHttpMessageHandler<>()` in `Program.cs`.

## Security

- Credentials are read from environment variables at startup. They are never written to disk by the server.
- All API requests use HTTPS with HTTP Basic auth (Base64-encoded API key).
- Tools are tagged with `ReadOnly` and `Destructive` attributes so MCP clients can permission categories of operation in bulk.
- The `.mcpb` files distributed in Releases do not contain credentials — the host injects them at runtime from the user's configuration.

## Licence

Proprietary — © FISCAL Technologies Ltd. All rights reserved.

## Support

- Internal: Infrastructure team at FISCAL Technologies.
- Issues: [GitHub Issues](https://github.com/fiscaltec/vitally-mcp/issues).
