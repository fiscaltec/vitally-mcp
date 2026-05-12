# Installation Methods Comparison

This document helps you choose the best installation method for the Vitally MCP server based on your use case.

## Quick Decision Guide

| Your Situation | Recommended Method |
|----------------|-------------------|
| End user who wants the easiest installation | **MCPB** |
| Developer testing changes to the server code | **Standalone Executable** |
| Distributing to multiple users | **MCPB** |
| Need to frequently update/rebuild | **Standalone Executable** |
| Production deployment | **MCPB** |
| Debugging or development | **Standalone Executable** |

---

## Method 1: MCPB Installation

### What is MCPB?

MCPB (MCP Bundle) is a packaged format that installs the MCP server into your system, making it available as a registered command.

### ✅ Advantages

- **Easiest for end users** - Just double-click to install
- **Clean installation** - Server is registered in the system
- **Simple configuration** - Use just the command name, not full paths
- **Professional distribution** - Single file to share
- **Automatic updates** - Users just install new `.mcpb` files
- **No path management** - Works from any location

### ❌ Disadvantages

- **Requires Node.js to build** - Need `npm` and `@anthropic-ai/mcpb`
- **Extra build step** - Takes longer to package
- **Less flexible for development** - Need to reinstall after each change
- **Windows-specific** - MCPB format is Windows-only currently

### Configuration Example

```json
{
  "mcpServers": {
    "vitally": {
      "command": "VitallyMcp",
      "env": {
        "VITALLY_API_KEY": "sk_live_...",
        "VITALLY_SUBDOMAIN": "fiscaltec"
      }
    }
  }
}
```

### When to Use

✅ **Use MCPB when:**
- Distributing to end users
- Want the simplest user experience
- Setting up production environments
- Users shouldn't need to know where files are located

❌ **Don't use MCPB when:**
- Actively developing/debugging the server
- Making frequent code changes
- Need to test different builds quickly

---

## Method 2: Standalone Executable

### What is it?

A self-contained executable file that runs directly without installation. You specify the full path to the `.exe` file.

### ✅ Advantages

- **Fast to build** - Just `dotnet publish` or run `Scripts\build-standalone.ps1`
- **No dependencies to build** - Don't need Node.js or MCPB CLI
- **Great for development** - Quick rebuild and test cycles
- **Multiple versions** - Can have different builds in different folders
- **Debugging friendly** - Can attach debuggers easily
- **Flexible** - Easy to move or copy to different locations

### ❌ Disadvantages

- **Path management** - Must specify full path in configuration
- **Path escaping** - Need double backslashes in JSON config
- **Manual updates** - Users need to rebuild or copy new exe
- **Less professional** - Not as clean as an installed package
- **Configuration changes** - Path might change between machines

### Configuration Example

```json
{
  "mcpServers": {
    "vitally": {
      "command": "C:\\Users\\YourUsername\\path\\to\\VitallyMcp\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe",
      "env": {
        "VITALLY_API_KEY": "sk_live_...",
        "VITALLY_SUBDOMAIN": "fiscaltec"
      }
    }
  }
}
```

### When to Use

✅ **Use Standalone when:**
- Developing or debugging the server
- Making frequent code changes
- Testing different configurations
- Don't want to install Node.js
- Need quick build/test cycles

❌ **Don't use Standalone when:**
- Distributing to non-technical users
- Want simple user experience
- Users shouldn't see file paths

---

## Build Commands Comparison

### MCPB Build

```powershell
# Prerequisites
npm install -g @anthropic-ai/mcpb

# Build
.\Scripts\build-mcpb.ps1

# Result
VitallyMcp-{version}.mcpb  (distributable package in Output/)
```

**Build time**: ~10-15 seconds (first time slower)

### Standalone Build

```powershell
# No prerequisites needed (just .NET SDK)

# Build with script
.\Scripts\build-standalone.ps1

# OR build manually
dotnet publish VitallyMcp.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output bin/Release/net10.0/win-x64/publish `
  /p:PublishSingleFile=true

# Result
VitallyMcp.exe  (in bin/Release/net10.0/win-x64/publish/)
```

**Build time**: ~5-8 seconds

---

## Configuration Comparison

### Side-by-Side Comparison

| Aspect | MCPB | Standalone Executable |
|--------|------|----------------------|
| **Command format** | `"VitallyMcp"` | `"C:\\full\\path\\to\\VitallyMcp.exe"` |
| **Path escaping** | Not needed | Required (double backslashes) |
| **Portability** | Same config works for all users | Path specific to each machine |
| **Installation** | Required (double-click .mcpb) | Not required |
| **Updates** | Install new .mcpb | Copy new .exe |

---

## Development Workflow Comparison

### MCPB Workflow

```powershell
# 1. Make code changes
# 2. Build MCPB
.\Scripts\build-mcpb.ps1

# 3. Uninstall old version (if needed)
# 4. Install new .mcpb (double-click from Output/)
# 5. Restart Claude Desktop
# 6. Test

# Time per iteration: ~2-3 minutes
```

### Standalone Workflow

```powershell
# 1. Make code changes
# 2. Build executable
.\Scripts\build-standalone.ps1

# 3. Restart Claude Desktop
# 4. Test

# Time per iteration: ~30-60 seconds
```

---

## Distribution Recommendations

### Internal Team Distribution

**For Developers:**
```
Share: Source code + Scripts/build-standalone.ps1
Method: Standalone Executable
Why: Developers need flexibility and quick iterations
```

**For End Users (Customer Success Team):**
```
Share: Pre-built .mcpb file from Output/ + configuration template
Method: MCPB
Why: Simple installation, no technical setup required
```

### Production Deployment

**Always use MCPB** for production deployments:
1. Build once: `.\Scripts\build-mcpb.ps1`
2. Test the .mcpb file from Output/
3. Distribute to all users
4. Provide configuration template with environment variables

---

## Switching Between Methods

### From Standalone to MCPB

1. Build the MCPB: `.\Scripts\build-mcpb.ps1`
2. Install the .mcpb file from Output/ (double-click)
3. Update your `claude_desktop_config.json`:
   ```json
   // Before (Standalone)
   "command": "C:\\long\\path\\to\\VitallyMcp.exe"

   // After (MCPB)
   "command": "VitallyMcp"
   ```
4. Restart Claude Desktop

### From MCPB to Standalone

1. Build standalone: `.\Scripts\build-standalone.ps1`
2. Update your `claude_desktop_config.json`:
   ```json
   // Before (MCPB)
   "command": "VitallyMcp"

   // After (Standalone)
   "command": "C:\\path\\to\\bin\\Release\\net10.0\\win-x64\\publish\\VitallyMcp.exe"
   ```
3. Restart Claude Desktop
4. Optional: Uninstall the MCPB if you want to free up space

**Note:** Both methods use the same `env` variables, so that part of the config stays the same.

---

## Summary Table

| Feature | MCPB | Standalone |
|---------|------|-----------|
| **Ease of use** | ★★★★★ | ★★★☆☆ |
| **Development speed** | ★★☆☆☆ | ★★★★★ |
| **Distribution** | ★★★★★ | ★★☆☆☆ |
| **Build complexity** | ★★★☆☆ | ★★★★★ |
| **Production ready** | ★★★★★ | ★★★☆☆ |
| **Configuration simplicity** | ★★★★★ | ★★★☆☆ |

---

## Recommendations by Role

### Infrastructure Engineers (You!)
- **Development**: Use Standalone
- **Testing**: Use Standalone or MCPB
- **Distribution**: Build MCPB for others

### Customer Success Team
- **Usage**: MCPB only
- **Installation**: Receive pre-built .mcpb file
- **Updates**: Install new .mcpb when provided

### Management/Leadership
- **Installation**: MCPB
- **Why**: Simplest, most professional experience

---

## Need Help Choosing?

**Ask yourself:**

1. **Are you writing code for this server?**
   - Yes → Standalone Executable
   - No → MCPB

2. **Are you distributing to others?**
   - Yes → MCPB
   - No → Either works

3. **Do you need to test changes frequently?**
   - Yes → Standalone Executable
   - No → MCPB

4. **Is this for production use?**
   - Yes → MCPB
   - No → Either works

Still unsure? Start with **Standalone** for development, switch to **MCPB** for distribution.
