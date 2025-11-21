# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Model Context Protocol (MCP) server implementation in C# that provides read-only access to the Vitally customer success platform. The server is packaged as an MCPB (MCP Bundle) for easy distribution and installation on Windows.

**Key characteristics:**
- Read-only API access to Vitally resources (accounts, organisations, users, conversations, notes, projects, tasks, admins)
- Windows-specific MCPB packaging
- .NET 10 single-file executable
- Credentials managed via environment variables (VITALLY_API_KEY, VITALLY_SUBDOMAIN)

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
   $env:VITALLY_SUBDOMAIN = "your-subdomain"
   ```

2. Run the published executable:
   ```powershell
   .\bin\Release\net10.0\win-x64\publish\VitallyMcp.exe
   ```

3. Configure Claude Desktop to use the full path to the executable

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
- `VITALLY_SUBDOMAIN` - Required subdomain (e.g., "fiscaltec")
- Throws InvalidOperationException with helpful messages if not set

### HTTP Service (VitallyService.cs)

Centralised HTTP client for Vitally REST API:
- Base URL constructed from subdomain: `https://{subdomain}.rest.vitally.io`
- Basic authentication using Base64-encoded API key
- **Client-side JSON filtering** to reduce response sizes for LLM consumption
- **Resource-specific default fields** optimised per resource type
- Two core methods:
  - `GetResourcesAsync()` - List resources with pagination, sorting, and filtering
  - `GetResourceByIdAsync()` - Get single resource by ID

**Vitally API Parameters:**
- Pagination uses `from` parameter (not `cursor`) - pass the `next` value from previous response
- Sorting via `sortBy` parameter: `"createdAt"` or `"updatedAt"` (default: updatedAt)
- Resource-specific filters (e.g., `status` for accounts: active, churned, activeOrChurned)

**Client-Side Filtering:**
- The Vitally API does NOT support field selection natively
- VitallyService implements client-side JSON filtering after receiving full API response
- Uses `System.Text.Json.JsonDocument` to parse and filter fields
- Only includes fields that actually exist on the resource (via TryGetProperty)
- Preserves pagination metadata (`next` field) in filtered responses
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
| **Projects** | id, name, createdAt, updatedAt, traits, accountId, organizationId, archivedAt |
| **Admins** | id, name, email |

These defaults balance usefulness (business context, relationships, key metrics) with response size (excluding large fields like full traits objects, rich text content).

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
        [Description("Filter by account status: 'active', 'churned', 'activeOrChurned'")] string? status = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status))
            additionalParams["status"] = status;

        return await vitallyService.GetResourcesAsync("accounts", limit, from, fields, sortBy, additionalParams);
    }
}
```

**Note:** The `status` parameter is specific to AccountsTools. Other resource types have the standard parameters (limit, from, fields, sortBy) without resource-specific filters.

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
4. Tools are automatically discovered via assembly scanning
5. Update `Output/mcpb/manifest.json` to document the new tools

## Important Notes

- **UK English**: Use UK spelling (organisations, authorisation, etc.) in all code comments and documentation
- **Read-only**: Never implement write/update/delete operations - this is by design for security
- **Environment variables**: Never hardcode credentials - always use environment variable loading
- **Error handling**: VitallyService uses EnsureSuccessStatusCode() - HTTP errors propagate to MCP client
- **Client-side filtering**: Field selection is done client-side (Vitally API doesn't support it natively)
- **Resource-specific defaults**: Each resource type has optimised default fields (see table above)
- **Field existence**: Only includes fields that actually exist on the resource - no null/undefined placeholders
- **Pagination**: Use `from` parameter (not `cursor`) - this matches the Vitally API spec
- **JSON responses**: Tools return filtered JSON strings to reduce LLM context usage
- **Windows-specific**: MCPB packages are Windows-only (win-x64/win-arm64 runtimes)
- **MCP SDK Preview**: Using preview SDK version 0.4.0-preview.3 - breaking changes possible

## Distribution

When distributing the MCPB:
1. Build using `.\Scripts\build-all.ps1` or `.\Scripts\build-mcpb.ps1`
2. Distribute the versioned `.mcpb` file from `Output/` directory (never commit `.mcpb` files to git)
3. Provide users with environment variable template
4. Users double-click to install and configure via Claude Desktop

The `.gitignore` is configured to exclude:
- `Output/mcpb/VitallyMcp.exe` - Temporary executable staging
- `Output/*.mcpb` - Generated MCPB packages

## Testing Considerations

- Manual testing requires actual Vitally credentials
- Test pagination by using low limit values (e.g., limit=5) and verify `from` parameter works with `next` cursor
- Test client-side field filtering by specifying various field combinations
- **Verify resource-specific defaults**: Check each resource type returns its tailored default field set when no fields specified
- **Verify field existence handling**: Confirm that non-existent fields are skipped (not returned as null)
- Test sortBy parameter with "createdAt" and "updatedAt" values
- For accounts, test status filter with: active, churned, activeOrChurned
- Test edge cases: Request fields that don't exist on a resource type (should be skipped gracefully)
- Verify error handling by testing with invalid IDs/missing env vars
- Check that filtered responses are significantly smaller than full API responses
- Compare default field sets across resource types to verify they're optimised correctly
