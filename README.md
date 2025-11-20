# Vitally MCP Server

A Model Context Protocol (MCP) server implementation in C# for integrating with the Vitally customer success platform. This server provides read-only access to Vitally data including accounts, organisations, users, conversations, notes, projects, tasks, and admins.

Packaged as an MCPB (MCP Bundle) for easy distribution and installation on Windows.

## Features

- **Read-only access** to all major Vitally resources
- **Pagination support** for efficient data retrieval
- **Field selection** for customised data responses
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
# Build for your current architecture
dotnet publish VitallyMcp.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output bin/Release/net10.0/win-x64/publish `
  /p:PublishSingleFile=true

# For ARM64 systems, use win-arm64 instead:
# --runtime win-arm64
```

The executable will be at: `bin/Release/net10.0/win-x64/publish/VitallyMcp.exe`

#### Configuring Claude Desktop for Standalone Executable

Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "vitally": {
      "command": "C:\\path\\to\\VitallyMcp\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe",
      "env": {
        "VITALLY_API_KEY": "sk_live_your_api_key_here",
        "VITALLY_SUBDOMAIN": "your-subdomain"
      }
    }
  }
}
```

**Important**: Replace `C:\\path\\to\\VitallyMcp` with the actual full path to your project directory. Note the double backslashes (`\\`) in the path.

#### Example with Actual Path

If your project is in `C:\Users\dsearle\Downloads\mcp-test\VitallyMcp`:

```json
{
  "mcpServers": {
    "vitally": {
      "command": "C:\\Users\\dsearle\\Downloads\\mcp-test\\VitallyMcp\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe",
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
   .\build-mcpb.ps1
   ```

   This will:
   - Detect your system architecture (x64 or ARM64)
   - Publish a self-contained executable
   - Create a `.mcpb` file in the project root

4. **Install** the generated `.mcpb` file (double-click)

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
Get more accounts using this cursor: eyJzb3J0VmFsdWU...
```

### Getting a Specific Resource

```
Get the Vitally account with ID abc123
```

## Tool Parameters

### List Tools

All list tools support the following parameters:

- **limit** (optional): Maximum number of items to return (default: 20, max: 100)
- **cursor** (optional): Pagination cursor from previous response
- **fields** (optional): Comma-separated list of fields to include (e.g., `"id,name,createdAt"`)

### Get Tools

All get tools support:

- **id** (required): The resource ID
- **fields** (optional): Comma-separated list of fields to include

## Response Format

All responses are returned as JSON strings containing:

```json
{
  "results": [
    {
      "id": "...",
      "name": "...",
      "createdAt": "..."
    }
  ],
  "next": "cursor-token-for-pagination",
  "atEnd": false
}
```

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
├── VitallyService.cs         # HTTP service for Vitally API calls
├── Tools/                     # MCP tool implementations
│   ├── AccountsTools.cs
│   ├── OrganizationsTools.cs
│   ├── UsersTools.cs
│   ├── ConversationsTools.cs
│   ├── NotesTools.cs
│   ├── ProjectsTools.cs
│   ├── TasksTools.cs
│   └── AdminsTools.cs
├── mcpb/                      # MCPB packaging
│   └── manifest.json          # Package manifest
├── build-mcpb.ps1            # Build script for creating MCPB
├── VitallyMcp.csproj         # Project file
└── README.md                  # This file
```

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

Run the build script:

```powershell
.\build-mcpb.ps1
```

The script will:
1. Auto-detect your system architecture (x64 or ARM64)
2. Publish a self-contained .NET application
3. Stage files in the `mcpb/server` directory
4. Create an installable `.mcpb` bundle

### Manual Build

For manual control:

```powershell
# Publish the application
dotnet publish VitallyMcp.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output bin/Release/net10.0/win-x64/publish `
  /p:PublishSingleFile=true

# Copy to MCPB directory
Copy-Item bin/Release/net10.0/win-x64/publish/VitallyMcp.exe mcpb/server/

# Create MCPB bundle
cd mcpb
mcpb pack
```

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

1. Build the MCPB package: `.\build-mcpb.ps1`
2. Distribute the `.mcpb` file to users
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
- ✅ Include: Source code, manifest.json, build scripts
- ❌ Exclude: Built `.mcpb` files, `mcpb/server/` directory, API credentials

The `.gitignore` file is already configured appropriately.

## Support

For issues, questions, or feature requests:
- **Internal**: Contact the Infrastructure team at Fiscal Technologies
- **Issues**: Check the build logs and Claude Desktop logs first
- **Updates**: Rebuild and redistribute the `.mcpb` file when source code changes

## License

This project is proprietary software developed for Fiscal Technologies. All rights reserved.

---

**Version**: 1.0.0
**Last Updated**: November 2025
**Built with**: .NET 10, MCP SDK 0.4.0-preview.3
