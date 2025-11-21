# Vitally MCP Server

A Model Context Protocol (MCP) server implementation in C# for integrating with the Vitally customer success platform. This server provides read-only access to Vitally data including accounts, organisations, users, conversations, notes, projects, tasks, and admins.

Packaged as an MCPB (MCP Bundle) for easy distribution and installation on Windows.

## Features

- **Read-only access** to all major Vitally resources
- **Pagination support** for efficient data retrieval
- **Client-side field filtering** to reduce response sizes for LLM consumption
- **Resource-specific intelligent defaults** - each resource type returns optimised fields (business metrics for accounts, dates for tasks, etc.)
- **Field existence handling** - only includes fields that actually exist (no null placeholders)
- **Sort ordering** by createdAt or updatedAt
- **Resource-specific filters** (e.g., account status filtering)
- **Built with .NET 10** and the official MCP C# SDK
- **Environment variable configuration** for secure credential management
- **Single-file executable** packaged as MCPB for easy distribution

## Prerequisites

### For End Users (Installing the MCPB)
- Windows 10/11 (x64 or ARM64)
- Claude Desktop or another MCP-compatible client
- Vitally API credentials

### For Developers (Building from Source)
- .NET 10.0 SDK ([Download](https://dotnet.microsoft.com/download/dotnet/10.0))
- Node.js LTS ([Download](https://nodejs.org/))
- PowerShell 7+

## Installation

### Option 1: Install Pre-built MCPB (Recommended for End Users)

1. **Download** the `.mcpb` file from your distribution source

2. **Double-click** the `.mcpb` file to install

3. **Configure** your MCP client (e.g., Claude Desktop) with environment variables:

   Edit `%APPDATA%\Claude\claude_desktop_config.json`:

   ```json
   {
     "mcpServers": {
       "vitally": {
         "command": "VitallyMcp",
         "env": {
           "VITALLY_API_KEY": "sk_live_your_api_key_here",
           "VITALLY_SUBDOMAIN": "your-subdomain"
         }
       }
     }
   }
   ```

4. **Restart** Claude Desktop

### Option 2: Use Standalone Executable (For Testing/Development)

This method doesn't require MCPB installation and is useful for development or testing.

#### Building the Standalone Executable

```powershell
# Build using the automated script (auto-detects architecture and bumps version)
.\Scripts\build-standalone.ps1

# Skip version bump if needed
.\Scripts\build-standalone.ps1 -SkipVersionBump

# Build for specific architecture
.\Scripts\build-standalone.ps1 -Architecture win-x64

# For ARM64 systems
.\Scripts\build-standalone.ps1 -Architecture win-arm64
```

The executable will be at: `bin/Release/net10.0/win-x64/publish/VitallyMcp.exe`

#### Configuring Claude Desktop for Standalone Executable

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "vitally": {
      "command": "C:\\Users\\YourUsername\\path\\to\\VitallyMcp\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe",
      "env": {
        "VITALLY_API_KEY": "sk_live_your_api_key_here",
        "VITALLY_SUBDOMAIN": "your-subdomain"
      }
    }
  }
}
```

**Important**: Replace `C:\\path\\to\\mcp` with the actual full path to your project directory. Note the double backslashes (`\\`) in the path.

#### Example with Actual Path

If your project is in `C:\\Users\\YourUsername\\Downloads\\VitallyMcp`:

```json
{
  "mcpServers": {
    "vitally": {
      "command": "C:\\Users\\YourUsername\\Downloads\\VitallyMcp\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe",
      "env": {
        "VITALLY_API_KEY": "sk_live_your_api_key_here",
        "VITALLY_SUBDOMAIN": "your-subdomain"
      }
    }
  }
}
```

### Option 3: Build MCPB from Source

1. **Clone or download** the project

2. **Install the MCPB CLI** (first time only):
   ```powershell
   npm install -g @anthropic-ai/mcpb
   ```

3. **Build the MCPB package**:
   ```powershell
   # Build everything (bumps version, builds standalone, and creates MCPB)
   .\Scripts\build-all.ps1

   # Or build just the MCPB package
   .\Scripts\build-mcpb.ps1

   # Skip version bump if needed
   .\Scripts\build-mcpb.ps1 -SkipVersionBump
   ```

   This will:
   - Bump the version number (revision by default)
   - Detect your system architecture (x64 or ARM64)
   - Publish a self-contained executable
   - Create a versioned `.mcpb` file in the `Output/` directory (e.g., `VitallyMcp-1.1.3.mcpb`)

4. **Install** the generated `.mcpb` file from `Output/` (double-click)

5. **Configure** as described in Option 1 above

## Configuration

### Required Environment Variables

The server requires two environment variables to be set in your MCP client configuration:

| Variable | Description | Example |
|----------|-------------|---------|
| `VITALLY_API_KEY` | Your Vitally API key | `sk_live_51f45f82...` |
| `VITALLY_SUBDOMAIN` | Your Vitally subdomain | `fiscaltec` |

### Finding Your Credentials

- **API Key**: Available in your Vitally account settings
- **Subdomain**: The subdomain in your Vitally URL (e.g., `fiscaltec` from `fiscaltec.vitally.io`)

## Available Tools

The Vitally MCP server exposes the following tools for each resource type:

### Accounts
- `ListAccounts` - List accounts with pagination and field selection
- `GetAccount` - Get a specific account by ID

### Organisations
- `ListOrganizations` - List organisations with pagination and field selection
- `GetOrganization` - Get a specific organisation by ID

### Users
- `ListUsers` - List users with pagination and field selection
- `GetUser` - Get a specific user by ID

### Conversations
- `ListConversations` - List conversations with pagination and field selection
- `GetConversation` - Get a specific conversation by ID

### Notes
- `ListNotes` - List notes with pagination and field selection
- `GetNote` - Get a specific note by ID

### Projects
- `ListProjects` - List projects with pagination and field selection
- `GetProject` - Get a specific project by ID

### Tasks
- `ListTasks` - List tasks with pagination and field selection
- `GetTask` - Get a specific task by ID

### Admins
- `ListAdmins` - List admins with pagination and field selection
- `GetAdmin` - Get a specific admin by ID

## Usage Examples

### Listing Accounts

```
Can you list the first 10 Vitally accounts?
```

### Getting Specific Fields

```
List Vitally organisations but only show me the id, name, and createdAt fields
```

### Pagination

```
Get more accounts using the pagination cursor: eyJzb3J0VmFsdWU...
```

**Note:** Use the `next` value from the previous response as the `from` parameter to get the next page of results.

### Getting a Specific Resource

```
Get the Vitally account with ID abc123
```

## Tool Parameters

### List Tools

All list tools support the following parameters:

- **limit** (optional): Maximum number of items to return (default: 20, max: 100)
- **from** (optional): Pagination cursor from previous response (use the `next` value)
- **fields** (optional): Comma-separated list of fields to include (e.g., `"id,name,createdAt"`). See resource-specific defaults below. **Note:** Field filtering is done client-side to reduce response sizes.
- **sortBy** (optional): Sort by `"createdAt"` or `"updatedAt"` (default: updatedAt)

**AccountsTools only:**
- **status** (optional): Filter by account status - `"active"` (default), `"churned"`, or `"activeOrChurned"`

### Get Tools

All get tools support:

- **id** (required): The resource ID
- **fields** (optional): Comma-separated list of fields to include. See resource-specific defaults below. **Note:** Field filtering is done client-side.

### Resource-Specific Default Fields

When no `fields` parameter is specified, each resource type returns an optimised field set:

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

These defaults balance usefulness with response size:
- **Business entities** (Accounts/Organizations) include health metrics and financial data
- **Activity items** (Tasks/Notes) include dates, assignments, and relationships
- **People** (Users/Admins) include contact information and activity timestamps
- Large fields like full traits objects and rich text content are excluded unless explicitly requested

## Response Format

All responses are returned as filtered JSON strings. By default (when no fields are specified), responses contain resource-specific optimised fields to reduce LLM context usage.

**Example: ListAccounts (default fields)**
```json
{
  "results": [
    {
      "id": "acc_123",
      "name": "Acme Corp",
      "createdAt": "2024-01-15T10:30:00Z",
      "updatedAt": "2024-11-20T14:22:00Z",
      "externalId": "SF-12345",
      "organizationId": "org_456",
      "healthScore": 8.5,
      "mrr": 5000,
      "accountOwnerId": "user_789",
      "lastSeenTimestamp": "2024-11-19T09:15:00Z"
    }
  ],
  "next": "cursor-token-for-pagination"
}
```

**Example: ListTasks (default fields)**
```json
{
  "results": [
    {
      "id": "task_123",
      "name": "Onboarding call",
      "createdAt": "2024-11-01T08:00:00Z",
      "updatedAt": "2024-11-15T16:30:00Z",
      "externalId": null,
      "dueDate": "2024-11-25",
      "completedAt": null,
      "assignedToId": "user_456",
      "accountId": "acc_789",
      "organizationId": "org_012",
      "archivedAt": null
    }
  ],
  "next": null
}
```

**Note:**
- The Vitally API does not support field filtering natively. Field selection is implemented client-side by parsing the full API response and extracting only the requested fields before returning to the LLM.
- Only fields that exist on the resource are included - non-existent fields are omitted (not returned as null).

## Architecture

The server is built using:

- **ModelContextProtocol (0.4.0-preview.3)** - Official MCP C# SDK
- **Microsoft.Extensions.Hosting (10.0.0)** - Application hosting and dependency injection
- **Microsoft.Extensions.Http (10.0.0)** - HTTP client factory for API calls
- **.NET 10.0** - Latest .NET runtime

### Project Structure

```
VitallyMcp/
├── Program.cs                 # Application entry point and MCP server setup
├── VitallyConfig.cs          # Configuration model with environment variable loading
├── VitallyService.cs         # HTTP service with client-side JSON filtering
├── Tools/                     # MCP tool implementations
│   ├── AccountsTools.cs       # Account tools with status filtering
│   ├── OrganizationsTools.cs
│   ├── UsersTools.cs
│   ├── ConversationsTools.cs
│   ├── NotesTools.cs
│   ├── ProjectsTools.cs
│   ├── TasksTools.cs
│   └── AdminsTools.cs
├── Scripts/                   # Build automation scripts
│   ├── bump-version.ps1       # Version bumping script
│   ├── build-standalone.ps1   # Standalone executable build
│   ├── build-mcpb.ps1        # MCPB package build
│   └── build-all.ps1         # Combined build script
├── Output/                    # Build output directory
│   ├── mcpb/                  # MCPB packaging files
│   │   ├── manifest.json      # Package manifest
│   │   ├── logo.png           # Package icon
│   │   └── VitallyMcp.exe     # Executable staging (gitignored)
│   └── VitallyMcp-*.mcpb     # Generated MCPB packages (gitignored)
├── VitallyMcp.csproj         # Project file
└── README.md                  # This file
```

**Key Implementation Details:**
- **VitallyService** implements client-side JSON filtering using `System.Text.Json.JsonDocument`
- Field filtering reduces response sizes before returning to LLM
- **Resource-specific default fields** optimised per resource type (see table above)
- Only fields that actually exist on the resource are included (via TryGetProperty)
- Pagination uses `from` parameter matching Vitally API spec

## Building MCPB Packages

### Prerequisites

1. Install Node.js:
   ```powershell
   winget install OpenJS.NodeJS.LTS
   ```

2. Install MCPB CLI (first time only):
   ```powershell
   npm install -g @anthropic-ai/mcpb
   ```

### Build Process

The project includes automated build scripts in the `Scripts/` directory:

#### Complete Build (Recommended)

```powershell
# Bumps version, builds standalone, and creates MCPB package
.\Scripts\build-all.ps1

# Bump minor version instead of revision
.\Scripts\build-all.ps1 -BumpType Minor

# Skip standalone build
.\Scripts\build-all.ps1 -SkipStandalone
```

#### MCPB Package Only

```powershell
# Build MCPB with automatic version bump
.\Scripts\build-mcpb.ps1

# Skip version bump
.\Scripts\build-mcpb.ps1 -SkipVersionBump

# Specify architecture
.\Scripts\build-mcpb.ps1 -Architecture win-arm64
```

The script will:
1. Bump the version number (revision by default)
2. Auto-detect your system architecture (x64 or ARM64)
3. Publish a self-contained .NET application
4. Stage files in the `Output/mcpb` directory (executable at top level)
5. Create a versioned `.mcpb` bundle in `Output/` (e.g., `VitallyMcp-1.1.3.mcpb`)
6. Clean up temporary files

#### Version Management

```powershell
# Manually bump version (revision, minor, or major)
.\Scripts\bump-version.ps1 -BumpType Revision  # 1.1.2 -> 1.1.3
.\Scripts\bump-version.ps1 -BumpType Minor     # 1.1.3 -> 1.2.0
.\Scripts\bump-version.ps1 -BumpType Major     # 1.2.0 -> 2.0.0
```

The version bump script updates both:
- `VitallyMcp.csproj` - Project version
- `Output/mcpb/manifest.json` - MCPB package version

## Security Considerations

### Environment Variables
- **Credentials are never stored in files** - all sensitive data is passed via environment variables
- Environment variables are configured in the MCP client (e.g., Claude Desktop config)
- The server reads credentials at startup from the environment

### Read-Only Operations
- The server provides **read-only access** only
- No create, update, or delete operations are available
- Minimises risk of accidental data modification

### HTTPS
- All API requests use HTTPS with Basic Authentication
- Credentials are Base64-encoded and sent over secure connections

### Best Practices
1. **Never commit** the `.mcpb` file with hardcoded credentials
2. **Use environment variables** in your MCP client configuration
3. **Rotate API keys** regularly
4. **Limit API key permissions** in Vitally to read-only access if possible

## Troubleshooting

### Server not appearing in Claude Desktop

1. Verify the MCPB was installed successfully (double-click should show installation progress)
2. Check that environment variables are correctly configured in `claude_desktop_config.json`
3. Ensure the variable names match exactly: `VITALLY_API_KEY` and `VITALLY_SUBDOMAIN`
4. Restart Claude Desktop after configuration changes
5. Check Claude Desktop logs for error messages

### Authentication errors

**Error: "Environment variable 'VITALLY_API_KEY' is required but not set"**
- Add the `VITALLY_API_KEY` environment variable to your MCP client config
- Ensure the value starts with `sk_live_`

**Error: "Environment variable 'VITALLY_SUBDOMAIN' is required but not set"**
- Add the `VITALLY_SUBDOMAIN` environment variable to your MCP client config
- Use only the subdomain (e.g., `fiscaltec`), not the full URL

**HTTP 401 Unauthorized**
- Verify your API key is correct and active in Vitally
- Ensure the API key has appropriate permissions

### Build errors

**"dotnet: command not found"**
- Install .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`

**"mcpb: command not found"**
- Install Node.js: `winget install OpenJS.NodeJS.LTS`
- Install MCPB CLI: `npm install -g @anthropic-ai/mcpb`

**Build fails with architecture errors**
- Specify architecture explicitly: `.\build-mcpb.ps1 -Architecture win-x64`
- Ensure you're using PowerShell 7+: `$PSVersionTable.PSVersion`

## Limitations

- **Read-only operations**: Only GET operations are supported
- **Windows only**: MCPB packages are currently Windows-specific
- **.NET 10 requirement**: The runtime requires .NET 10.0
- **No message/NPS response endpoints**: These endpoints were not accessible during development
- **Preview SDK**: The MCP C# SDK is in preview and may have breaking changes

## Distribution

### For Internal Use

1. Build the MCPB package: `.\Scripts\build-all.ps1` or `.\Scripts\build-mcpb.ps1`
2. Distribute the versioned `.mcpb` file from `Output/` directory to users
3. Provide installation instructions (double-click to install)
4. Share the required environment variables template:

   ```json
   "env": {
     "VITALLY_API_KEY": "sk_live_...",
     "VITALLY_SUBDOMAIN": "your-subdomain"
   }
   ```

### Version Control

When committing to version control:
- ✅ Include: Source code, `Output/mcpb/manifest.json`, build scripts in `Scripts/`
- ❌ Exclude: Built `.mcpb` files, `Output/mcpb/VitallyMcp.exe`, `Output/*.mcpb`, API credentials

The `.gitignore` file is already configured appropriately.

## Support

For issues, questions, or feature requests:
- **Internal**: Contact the Infrastructure team at Fiscal Technologies
- **Issues**: Check the build logs and Claude Desktop logs first
- **Updates**: Rebuild and redistribute the `.mcpb` file when source code changes

## License

This project is proprietary software developed for Fiscal Technologies. All rights reserved.

---

**Version**: 1.1.5
**Last Updated**: November 2024
**Built with**: .NET 10, MCP SDK 0.4.0-preview.3
