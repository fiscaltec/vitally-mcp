# Quick Start Guide - Vitally MCP Server

Get started with the Vitally MCP server in 5 minutes or less!

Choose your installation method:
- **[Method A: MCPB Installation](#method-a-mcpb-installation-recommended)** (Recommended for end users)
- **[Method B: Standalone Executable](#method-b-standalone-executable-for-developers)** (For developers/testing)

---

## Method A: MCPB Installation (Recommended)

### Step 1: Install the MCPB

1. Download the `Vitally.mcpb` file
2. Double-click it to install
3. Wait for the installation to complete

### Step 2: Configure Claude Desktop

1. Open your Claude Desktop configuration file:
   - Press `Win+R`
   - Type: `%APPDATA%\Claude\claude_desktop_config.json`
   - Press Enter

2. Add the Vitally server configuration:

   ```json
   {
     "mcpServers": {
       "vitally": {
         "command": "VitallyMcp",
         "env": {
           "VITALLY_API_KEY": "sk_live_YOUR_API_KEY_HERE",
           "VITALLY_SUBDOMAIN": "YOUR_SUBDOMAIN_HERE"
         }
       }
     }
   }
   ```

3. Replace the placeholder values:
   - `YOUR_API_KEY_HERE` → Your Vitally API key (from Vitally settings)
   - `YOUR_SUBDOMAIN_HERE` → Your subdomain (e.g., `fiscaltec`)

4. Save the file

### Step 3: Restart Claude Desktop

1. Close Claude Desktop completely
2. Open it again
3. The Vitally server should now be available!

### Step 4: Test It

Ask Claude:
```
Can you list 5 Vitally accounts?
```

If you see account data, you're all set! 🎉

---

## Method B: Standalone Executable (For Developers)

### Step 1: Build the Executable

```powershell
# Quick build with helper script
.\build-standalone.ps1

# OR manually build
dotnet publish VitallyMcp.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output bin/Release/net10.0/win-x64/publish `
  /p:PublishSingleFile=true
```

The executable will be at: `bin/Release/net10.0/win-x64/publish/VitallyMcp.exe`

### Step 2: Configure Claude Desktop

1. Open your Claude Desktop configuration file:
   - Press `Win+R`
   - Type: `%APPDATA%\Claude\claude_desktop_config.json`
   - Press Enter

2. Add the Vitally server configuration with the full path to the executable:

   ```json
   {
     "mcpServers": {
       "vitally": {
         "command": "C:\\Users\\YourUsername\\path\\to\\VitallyMcp\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe",
         "env": {
           "VITALLY_API_KEY": "sk_live_YOUR_API_KEY_HERE",
           "VITALLY_SUBDOMAIN": "YOUR_SUBDOMAIN_HERE"
         }
       }
     }
   }
   ```

   **Important**:
   - Use the **full path** to VitallyMcp.exe
   - Use **double backslashes** (`\\`) in the path
   - Replace `YourUsername` and the path with your actual location

### Step 3: Restart Claude Desktop

1. Close Claude Desktop completely
2. Open it again
3. The Vitally server should now be available!

### Step 4: Test It

Ask Claude:
```
Can you list 5 Vitally accounts?
```

If you see account data, you're all set! 🎉

---

## For Developers: Building MCPB from Source

```powershell
# 1. Install prerequisites (first time only)
winget install OpenJS.NodeJS.LTS
npm install -g @anthropic-ai/mcpb

# 2. Build the MCPB package
.\build-mcpb.ps1

# 3. Install the generated .mcpb file
# (Double-click the .mcpb file that appears in the project root)

# 4. Configure as shown in "Method A" above
```

## Troubleshooting

### "Environment variable 'VITALLY_API_KEY' is required but not set"

✅ **Fix**: Add the `env` section to your `claude_desktop_config.json` with your API credentials

### "Cannot find command 'VitallyMcp'"

✅ **Fix**: Ensure you've installed the `.mcpb` file by double-clicking it

### Changes not appearing

✅ **Fix**: Completely close and restart Claude Desktop

## What's Next?

Once configured, you can:

- **List resources**: "Show me Vitally organisations"
- **Get specific data**: "Get account abc123 from Vitally"
- **Filter fields**: "List users but only show id, name, and email"
- **Paginate**: "Show me more accounts" (Claude will use the cursor automatically)

## Need Help?

- 📖 See [README.md](README.md) for detailed documentation
- 🐛 Check Claude Desktop logs for error messages
- 💬 Contact your Infrastructure team

---

**Quick Reference - Environment Variables**

| Variable | Example | Where to Find |
|----------|---------|---------------|
| `VITALLY_API_KEY` | `sk_live_51f45f82...` | Vitally → Settings → API |
| `VITALLY_SUBDOMAIN` | `fiscaltec` | Your Vitally URL subdomain |
