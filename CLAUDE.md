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

# Release build (single-file executable)
dotnet publish VitallyMcp.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output bin/Release/net10.0/win-x64/publish `
  /p:PublishSingleFile=true

# For ARM64 systems, use win-arm64 instead
```

### Creating MCPB Package

```powershell
# Build and package as MCPB (auto-detects architecture)
.\build-mcpb.ps1

# Specify architecture explicitly
.\build-mcpb.ps1 -Architecture win-x64
```

This creates a `.mcpb` file in the project root that can be double-clicked to install.

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
- Two core methods:
  - `GetResourcesAsync()` - List resources with pagination/field selection
  - `GetResourceByIdAsync()` - Get single resource by ID

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
        [Description("Pagination cursor...")] string? cursor = null,
        [Description("Comma-separated fields...")] string? fields = null)
    {
        return await vitallyService.GetResourcesAsync("accounts", limit, cursor, fields);
    }
}
```

### MCPB Packaging (mcpb/)

The MCPB manifest (`mcpb/manifest.json`) defines:
- Server metadata (name, version, description, author)
- Binary entry point (`server/VitallyMcp.exe`)
- Environment variable mappings for user configuration
- Tool descriptions for MCP clients

The `build-mcpb.ps1` script:
1. Auto-detects system architecture (x64/ARM64)
2. Publishes self-contained executable
3. Stages files in `mcpb/server/`
4. Runs `mcpb pack` to create installable bundle

## Adding New Resource Types

To add support for a new Vitally resource:

1. Create `Tools/{ResourceName}Tools.cs` following the pattern in AccountsTools.cs
2. Implement `List{ResourceName}` and `Get{ResourceName}` methods
3. Use VitallyService with appropriate resource type string
4. Tools are automatically discovered via assembly scanning
5. Update mcpb/manifest.json to document the new tools

## Important Notes

- **UK English**: Use UK spelling (organisations, authorisation, etc.) in all code comments and documentation
- **Read-only**: Never implement write/update/delete operations - this is by design for security
- **Environment variables**: Never hardcode credentials - always use environment variable loading
- **Error handling**: VitallyService uses EnsureSuccessStatusCode() - HTTP errors propagate to MCP client
- **JSON responses**: All tool responses return raw JSON strings from Vitally API
- **Windows-specific**: MCPB packages are Windows-only (win-x64/win-arm64 runtimes)
- **MCP SDK Preview**: Using preview SDK version 0.4.0-preview.3 - breaking changes possible

## Distribution

When distributing the MCPB:
1. Build using `.\build-mcpb.ps1`
2. Distribute the `.mcpb` file (never commit it to git)
3. Provide users with environment variable template
4. Users double-click to install and configure via Claude Desktop

## Testing Considerations

- Manual testing requires actual Vitally credentials
- Test pagination by using low limit values (e.g., limit=5)
- Test field selection with valid field names from Vitally API docs
- Verify error handling by testing with invalid IDs/missing env vars
