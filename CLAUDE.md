# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Model Context Protocol (MCP) server implementation in C# that provides full CRUD access to the Vitally customer success platform. The server is packaged as an MCPB (MCP Bundle) for easy distribution and installation on Windows.

**Key characteristics:**
- Full CRUD API access to Vitally resources (accounts, organisations, users, conversations, notes, projects, tasks, admins, NPS responses, project templates, project categories, messages, custom objects, meetings — including participants and transcripts — custom traits, custom surveys)
- Permission management via ReadOnly and Destructive flags for MCP clients
- Windows-specific MCPB packaging
- .NET 10 single-file executable
- Built on the official `ModelContextProtocol` C# SDK (1.3.0 GA)
- Multi-region support: EU (default, `rest.vitally-eu.io`) and US (`{subdomain}.rest.vitally.io`) via the `VITALLY_REGION` env var
- Rate-limit-aware HTTP pipeline: auto-retries on `429 Too Many Requests` and logs a warning when `X-RateLimit-Remaining` drops below threshold
- In-process update check (`Check_for_updates` tool) against GitHub Releases
- Distributed for both **Claude Desktop** (as `.mcpb`) and **Claude Code** (as a standalone `.exe`) from the same GitHub Release
- Credentials and region managed via environment variables (VITALLY_API_KEY, VITALLY_SUBDOMAIN, VITALLY_REGION)

## Common Development Commands

### Building and Publishing

```powershell
# Debug build
dotnet build

# Complete build (bumps version, builds standalone, creates MCPB)
.\Scripts\build-all.ps1

# Build standalone executable only
.\Scripts\build-standalone.ps1

# Build MCPB package only
.\Scripts\build-mcpb.ps1

# Skip version bump if needed
.\Scripts\build-standalone.ps1 -SkipVersionBump
.\Scripts\build-mcpb.ps1 -SkipVersionBump
```

### Version Management

```powershell
# Bump version (updates both .csproj and manifest.json)
.\Scripts\bump-version.ps1                      # Bumps revision (1.1.2 -> 1.1.3)
.\Scripts\bump-version.ps1 -BumpType Minor      # Bumps minor (1.1.3 -> 1.2.0)
.\Scripts\bump-version.ps1 -BumpType Major      # Bumps major (1.2.0 -> 2.0.0)
```

### Creating MCPB Package

```powershell
# Build and package as MCPB (auto-detects architecture, bumps version)
.\Scripts\build-mcpb.ps1

# Specify architecture explicitly
.\Scripts\build-mcpb.ps1 -Architecture win-x64

# Skip version bump
.\Scripts\build-mcpb.ps1 -SkipVersionBump
```

This creates a versioned `.mcpb` file in the `Output/` directory (e.g., `VitallyMcp-1.1.3.mcpb`) that can be double-clicked to install.

### Testing the Server

To test the server locally without MCPB installation:

1. Set environment variables in PowerShell:
   ```powershell
   $env:VITALLY_API_KEY = "sk_live_your_key"
   # VITALLY_REGION defaults to "EU"; only set it if you're on a US tenant
   # $env:VITALLY_REGION   = "US"
   # $env:VITALLY_SUBDOMAIN = "your-subdomain"  # required only when VITALLY_REGION=US
   ```

2. Run the published executable:
   ```powershell
   .\bin\Release\net10.0\win-x64\publish\VitallyMcp.exe
   ```

3. Configure Claude Desktop or Claude Code to use the full path to the executable (see "Installing for End Users" below).

## Installing for End Users

Releases are published at https://github.com/fiscaltec/vitally-mcp/releases. Each release contains four artefacts plus a `SHA256SUMS.txt`:

| Artefact | Audience |
|---|---|
| `VitallyMcp-{version}-win-x64.mcpb` | Claude Desktop on Intel/AMD64 |
| `VitallyMcp-{version}-win-arm64.mcpb` | Claude Desktop on ARM64 |
| `VitallyMcp-{version}-win-x64.exe` | Claude Code (or any direct-launch MCP host) on Intel/AMD64 |
| `VitallyMcp-{version}-win-arm64.exe` | Claude Code on ARM64 |

### Claude Desktop

Download the `.mcpb` for your architecture and double-click to install. Claude Desktop prompts for the env-var values declared in `manifest.json` (`VITALLY_API_KEY`, plus optional `VITALLY_REGION` and `VITALLY_SUBDOMAIN`).

To update: download the new `.mcpb` and double-click — Claude Desktop replaces the previous version.

### Claude Code

Claude Code doesn't use the MCPB format; it expects a path to an executable in its MCP config. Download the `.exe` for your architecture (e.g. to `C:\Tools\VitallyMcp\VitallyMcp.exe`), then add to your Claude Code MCP config (`~/.claude.json` or project-local `.mcp.json`):

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

If your tenant is on the US region, set `"VITALLY_REGION": "US"` and add `"VITALLY_SUBDOMAIN": "your-subdomain"` to the `env` block.

To update: download the new `.exe` and replace the one referenced by `command`. No config change required as long as the path stays the same.

### Updates

Both clients can invoke the `Check_for_updates` MCP tool to compare their running version against the latest GitHub Release. The tool returns the current/latest versions, an `isUpToDate` flag, the release page URL, and pre-resolved download URLs matching the running architecture.

## Architecture

### MCP Server Setup (Program.cs)

The server uses the official MCP C# SDK with automatic tool discovery:
- Logs to stderr for MCP protocol compatibility
- VitallyConfig loaded from environment variables
- HttpClient injected via DI for VitallyService
- Tools auto-discovered via `WithToolsFromAssembly()` attribute scanning

### Configuration (VitallyConfig.cs)

Environment variable loading with validation:
- `VITALLY_API_KEY` - Required API key (format: sk_live_*)
- `VITALLY_REGION` - Optional, `EU` (default) or `US`. Case-insensitive and trimmed. Invalid values throw.
- `VITALLY_SUBDOMAIN` - Required only when `VITALLY_REGION` is `US` (e.g., "fiscaltec"). Ignored for `EU` because that region uses a single shared host with no subdomain prefix.
- Throws `InvalidOperationException` with helpful messages if required values are missing for the chosen region

### HTTP Service (VitallyService.cs)

Centralised HTTP client for Vitally REST API:
- Base URL depends on `VitallyConfig.Region` (default `EU`):
  - EU: `https://rest.vitally-eu.io` (single shared host, no subdomain — matches Vitally's docs)
  - US: `https://{subdomain}.rest.vitally.io`
- Basic authentication using Base64-encoded API key
- **Client-side JSON filtering** to reduce response sizes for LLM consumption
- **Resource-specific default fields** optimised per resource type
- Standard methods (apply field/trait filtering and the `{results, next}` envelope):
  - `GetResourcesAsync()` - List resources with pagination, sorting, and filtering
  - `GetResourceByIdAsync()` - Get single resource by ID
  - `CreateResourceAsync()` / `UpdateResourceAsync()` / `DeleteResourceAsync()` - POST / PUT / DELETE the standard `/resources/{type}[/{id}]` paths
- Raw pass-through methods (no field filtering — for endpoints whose response shape is not the standard `{results, next}` envelope, e.g. surveys returning `{data, next}`, customFields returning a bare array, or for sub-resource paths like meeting participants):
  - `GetRawAsync(path, queryParams)` - GET with URL-encoded query string
  - `PostRawAsync(path, jsonBody)` - POST raw JSON
  - `DeleteRawAsync(path)` - DELETE arbitrary path

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
- VitallyService implements client-side JSON filtering after receiving full API response
- Uses `System.Text.Json.JsonDocument` to parse and filter fields and traits
- Only includes fields that actually exist on the resource (via TryGetProperty)
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
- Dependency injection: VitallyService injected as method parameter
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

**Note:** The `status` parameter is specific to AccountsTools, and `archived` is specific to MeetingsTools. The `traits` parameter is available for resources that support traits (Accounts, Organizations, Users, Tasks, Notes, Projects, Meetings, Project Templates). Other resource types have the standard parameters (limit, from, fields, sortBy).

**Raw pass-through tools:** `CustomTraitsTools`, `SurveysTools`, and the participant/transcript methods on `MeetingsTools` call `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` directly. They do not accept a `fields` parameter because Vitally returns these endpoints with a non-standard JSON envelope (`{data}` for surveys, bare arrays for `customFields`).

### Build Scripts (Scripts/)

The project includes several PowerShell build scripts:

**bump-version.ps1**:
- Reads current version from `VitallyMcp.csproj`
- Increments version based on type (Revision, Minor, Major)
- Updates both `.csproj` and `Output/mcpb/manifest.json`
- Returns new version for use by other scripts

**build-standalone.ps1**:
- Bumps revision version (unless -SkipVersionBump specified)
- Publishes self-contained executable for specified/detected architecture
- Outputs to `bin/{Configuration}/net10.0/{Architecture}/publish/`

**build-mcpb.ps1**:
- Bumps revision version (unless -SkipVersionBump specified)
- Publishes self-contained executable
- Stages files in `Output/mcpb/` (executable at top level)
- Runs `mcpb pack` to create versioned bundle
- Moves `.mcpb` file to `Output/VitallyMcp-{version}.mcpb`
- Cleans up temporary files

**build-all.ps1**:
- Bumps version once (revision by default, configurable via -BumpType)
- Optionally builds standalone executable
- Builds MCPB package
- Comprehensive build for release distribution

### MCPB Packaging (Output/mcpb/)

The MCPB manifest (`Output/mcpb/manifest.json`) defines:
- Server metadata (name, version, description, author)
- Binary entry point (`VitallyMcp.exe` at top level)
- Environment variable mappings for user configuration
- Tool descriptions for MCP clients

Build process:
1. Version is bumped automatically (or skipped with -SkipVersionBump)
2. Auto-detects system architecture (x64/ARM64)
3. Publishes self-contained executable
4. Stages files in `Output/mcpb/` (executable at top level)
5. Runs `mcpb pack` to create versioned bundle (e.g., `VitallyMcp-1.1.3.mcpb`)
6. Cleans up temporary executable from mcpb directory

## Adding New Resource Types

To add support for a new Vitally resource:

1. Create `Tools/{ResourceName}Tools.cs` following the pattern in AccountsTools.cs
2. Implement `List{ResourceName}` and `Get{ResourceName}` methods
3. Use VitallyService with appropriate resource type string
4. **If the endpoint returns the standard `{results, next}` envelope:** add an entry to `ResourceDefaultFields` in `VitallyService.cs` with the optimised default field set
5. **If the endpoint returns a non-standard envelope** (e.g. `{data}`) or is a sub-resource path (e.g. `meetings/:id/participants`): use `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` — these bypass client-side filtering and return the body unchanged
6. **For sub-paths under an existing resource** (e.g. `admins/search`): add an explicit entry to `ResourceDefaultFields` for the full path — the lookup is exact-match, not prefix-match
7. Tools are automatically discovered via assembly scanning
8. Add a matching `Tools/{ResourceName}ToolsTests.cs` under `VitallyMcp.Tests/Tools/`
9. Update `Output/mcpb/manifest.json` to document the new tools

## Important Notes

- **UK English**: Use UK spelling (organisations, authorisation, etc.) in all code comments and documentation
- **Permission management**: Tools use ReadOnly = true flag for GET/LIST operations and Destructive = true flag for CREATE/UPDATE/DELETE operations. This allows MCP clients to bulk enable/disable operations by permission level.
- **Write operations**: All resources support full CRUD operations (where applicable). JSON body parameters accept complete request bodies for create/update operations.
- **Environment variables**: Never hardcode credentials - always use environment variable loading
- **Error handling**: VitallyService uses EnsureSuccessStatusCode() - HTTP errors propagate to MCP client
- **Client-side filtering**: Field and trait selection is done client-side (Vitally API doesn't support it natively)
- **Trait filtering**: Traits are excluded by default - use traits parameter to include specific trait keys (requires "traits" in fields parameter)
- **Resource-specific defaults**: Each resource type has optimised default fields (see table above)
- **Field existence**: Only includes fields that actually exist on the resource - no null/undefined placeholders
- **Pagination**: Use `from` parameter (not `cursor`) - this matches the Vitally API spec
- **JSON responses**: Tools return filtered JSON strings to reduce LLM context usage
- **Windows-specific**: MCPB packages are Windows-only (win-x64/win-arm64 runtimes)
- **MCP SDK**: Using `ModelContextProtocol` 1.3.0 GA. The historical preview-to-GA upgrade from 0.4.0-preview.3 was source-compatible for this project's surface (stdio transport + attribute-based tools).

## Distribution

When distributing the MCPB:
1. Build using `.\Scripts\build-all.ps1` or `.\Scripts\build-mcpb.ps1`
2. Distribute the versioned `.mcpb` file from `Output/` directory (never commit `.mcpb` files to git)
3. Provide users with environment variable template
4. Users double-click to install and configure via Claude Desktop

The `.gitignore` is configured to exclude:
- `Output/mcpb/VitallyMcp.exe` - Temporary executable staging
- `Output/*.mcpb` - Generated MCPB packages

## Testing

The `VitallyMcp.Tests` project contains the automated test suite (xUnit + FluentAssertions + Moq).

```powershell
# Run the full suite
dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal

# Run a single test class
dotnet test --filter "FullyQualifiedName~MeetingsToolsTests"
```

**Coverage:**
- `VitallyConfigTests` — environment variable loading and validation
- `VitallyServiceTests` — JSON field/trait filtering, pagination, resource-specific defaults, plus all six service methods (`GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`, `GetRawAsync`, `PostRawAsync`, `DeleteRawAsync`) including HTTP-verb / URL / auth-header verification via Moq protected verification
- `Tools/*ToolsTests` — one test class per `Tools/*Tools.cs`, covering every public `[McpServerTool]` method (list/get/create/update/delete plus sub-resources)

**When adding a new tool method:** add a matching test in the appropriate `*ToolsTests.cs` file. Use `TestHelpers.CreateMockHttpClient` (no URL assertions) or `TestHelpers.CreateMockHttpClientWithHandler` (when you want to assert verb + path).

**Manual testing considerations** (require live Vitally credentials):
- Test pagination by using low limit values (e.g., limit=5) and verify `from` parameter works with `next` cursor
- Test client-side field filtering by specifying various field combinations
- Test trait filtering by combining `fields="traits"` with `traits="trait1,trait2"`
- For accounts, test the status filter with: `active`, `churned`, `activeOrChurned`
- For meetings, test the `archived` filter
- Verify error handling with invalid IDs/missing env vars
